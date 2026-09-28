using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// OpenAI-compatible API provider (supports OpenAI, local servers like Ollama, LM Studio, etc.).
    /// </summary>
    public sealed class OpenAiCompatibleProvider : IAiProvider
    {
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        private readonly SemaphoreSlim _chatSemaphore = new SemaphoreSlim(1, 1);  // Serialize chat requests

        public OpenAiCompatibleProvider()
        {
            // 5 minute timeout for reasoning models (cloud + reasoning can be slow)
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public async Task<AiResponse> GetChatCompletionAsync(
            string systemPrompt,
            List<ChatMessage> messages,
            List<DocumentChunk> contextChunks,
            AiProviderConfig config)
        {
            return await GetChatCompletionAsync(systemPrompt, messages, contextChunks, "", config);
        }

        /// <summary>
        /// Gets a chat completion with structured output and source references.
        /// </summary>
        public async Task<AiResponse> GetChatCompletionAsync(
            string systemPrompt,
            List<ChatMessage> messages,
            List<DocumentChunk> contextChunks,
            string sourceReferences,
            AiProviderConfig config)
        {
            await _chatSemaphore.WaitAsync();
            try
            {
                var requestBody = BuildRequest(systemPrompt, messages, sourceReferences, config);

                var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.BaseUrl.TrimEnd('/')}/chat/completions")
                {
                    Content = content
                };
                // For Ollama (localhost), use dummy key if empty
                var apiKey = string.IsNullOrWhiteSpace(config.ApiKey) && config.BaseUrl.Contains("localhost") 
                    ? "ollama" 
                    : config.ApiKey;
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                // Retry logic for 429/503
                var response = await SendWithRetryAsync(request, config);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw MapError(response.StatusCode, responseJson, config);
                }

                return ParseResponse(responseJson, config);
            }
            finally
            {
                _chatSemaphore.Release();
            }
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, AiProviderConfig config)
        {
            const int maxRetries = 2;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                var response = await _httpClient.SendAsync(request);
                
                // Retry on 429 (rate limit) or 503 (service unavailable)
                if ((response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || 
                     response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable) && 
                    attempt < 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)); // Short backoff
                    continue;
                }
                return response;
            }
            // Should not reach here
            return await _httpClient.SendAsync(request);
        }

        public async Task<bool> IsAvailableAsync(AiProviderConfig config)
        {
            // For Ollama (localhost), don't require API key
            var apiKey = string.IsNullOrWhiteSpace(config.ApiKey) && config.BaseUrl.Contains("localhost") 
                ? "ollama" 
                : config.ApiKey;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.BaseUrl.TrimEnd('/')}/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private object BuildRequest(
            string systemPrompt,
            List<ChatMessage> messages,
            string sourceReferences,
            AiProviderConfig config)
        {
            var fullSystemPrompt = systemPrompt + "\n\n" + sourceReferences;

            var requestMessages = new List<object>
            {
                new { role = "system", content = fullSystemPrompt }
            };

            // Add conversation history (limit to last 6 messages for context)
            var recentMessages = messages.Where(m => m.MessageRole != ChatMessage.Role.System).TakeLast(6);
            foreach (var msg in recentMessages)
            {
                requestMessages.Add(new { role = msg.MessageRole.ToString().ToLowerInvariant(), content = msg.Content });
            }

            var request = new
            {
                model = config.Model,
                messages = requestMessages,
                temperature = config.Temperature,
                max_tokens = config.MaxTokens,
                top_p = config.TopP,
                response_format = new { type = "json_object" }
            };

            // Add reasoning_effort if specified (for reasoning models like gpt-oss)
            if (!string.IsNullOrEmpty(config.ReasoningEffort))
            {
                // Use reflection to add optional field
                var dict = new Dictionary<string, object>
                {
                    ["model"] = config.Model,
                    ["messages"] = requestMessages,
                    ["temperature"] = config.Temperature,
                    ["max_tokens"] = config.MaxTokens,
                    ["top_p"] = config.TopP,
                    ["response_format"] = new { type = "json_object" }
                };
                dict["reasoning_effort"] = config.ReasoningEffort;
                return dict;
            }

            return new
            {
                model = config.Model,
                messages = requestMessages,
                temperature = config.Temperature,
                max_tokens = config.MaxTokens,
                top_p = config.TopP,
                response_format = new { type = "json_object" }
            };
        }

        private AiResponse ParseResponse(string json, AiProviderConfig config)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var choice = root.GetProperty("choices")[0];
                var message = choice.GetProperty("message");
                
                // Handle reasoning field (for reasoning models like gpt-oss)
                // We ignore it for display but log it for debugging
                if (message.TryGetProperty("reasoning", out var reasoningProp))
                {
                    var reasoning = reasoningProp.GetString() ?? "";
                    // Log reasoning for debugging (behind debug switch in production)
                    System.Diagnostics.Debug.WriteLine($"[AI Reasoning]: {reasoning}");
                }

                var content = message.GetProperty("content").GetString() ?? "";
                
                // Check for empty content with finish_reason = length
                var finishReason = choice.TryGetProperty("finish_reason", out var frProp) ? frProp.GetString() : "";
                if (string.IsNullOrEmpty(content) && finishReason == "length")
                {
                    return new AiResponse 
                    { 
                        Answer = "Answer was cut off; try a shorter question or raise the token limit.", 
                        Sources = new List<AiSource>() 
                    };
                }

                return ParseStructuredContent(content);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AI Parse Error]: {ex.Message}");
                // Fallback: treat entire response as answer
                return new AiResponse { Answer = json, Sources = new List<AiSource>() };
            }
        }

        private static Exception MapError(System.Net.HttpStatusCode statusCode, string responseJson, AiProviderConfig config)
        {
            var isLocalhost = config.BaseUrl.Contains("localhost");
            var isCloudModel = config.IsCloudModel;

            return statusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => 
                    new HttpRequestException(isCloudModel 
                        ? "Not signed in to Ollama cloud. Run: ollama signin"
                        : "Authentication failed. Check your API key."),
                
                System.Net.HttpStatusCode.NotFound => 
                    new HttpRequestException("Model not found. " + (isLocalhost 
                        ? $"Run: ollama pull {config.Model}" 
                        : "Check model name and availability.")),
                
                System.Net.HttpStatusCode.TooManyRequests => 
                    new HttpRequestException("Ollama cloud is busy. Try again in a moment."),
                
                System.Net.HttpStatusCode.ServiceUnavailable => 
                    new HttpRequestException("Ollama cloud is busy. Try again in a moment."),
                
                _ => new HttpRequestException($"AI API error: {statusCode} - {responseJson}")
            };
        }

        private AiResponse ParseStructuredContent(string content)
        {
            // Handle markdown code fences around JSON
            var jsonContent = ExtractJsonFromMarkdown(content);
            
            try
            {
                using var doc = JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;

                var answer = root.TryGetProperty("answer", out var answerProp) ? answerProp.GetString() ?? "" : "";
                var sources = new List<AiSource>();

                if (root.TryGetProperty("sources", out var sourcesProp) && sourcesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var src in sourcesProp.EnumerateArray())
                    {
                        sources.Add(new AiSource
                        {
                            SourceId = src.TryGetProperty("sourceId", out var sid) ? sid.GetString() ?? "" : "",
                            PageNumber = src.TryGetProperty("page", out var p) ? p.GetInt32() : 0,
                            PageIndex = src.TryGetProperty("page", out var pi) ? pi.GetInt32() - 1 : -1,
                            Quote = src.TryGetProperty("quote", out var q) ? q.GetString() ?? "" : "",
                            Reason = src.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : ""
                        });
                    }
                }

                return new AiResponse { Answer = answer, Sources = sources };
            }
            catch
            {
                // Fallback: treat entire response as answer (no sources)
                return new AiResponse { Answer = content, Sources = new List<AiSource>() };
            }
        }

        private static string ExtractJsonFromMarkdown(string content)
        {
            // Remove markdown code fences
            var fencedPattern = @"```(?:json)?\s*(\{[\s\S]*?\})\s*```";
            var match = Regex.Match(content, fencedPattern, RegexOptions.Singleline);
            if (match.Success)
                return match.Groups[1].Value.Trim();

            // Try to find JSON object directly
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start >= 0 && end > start)
                return content.Substring(start, end - start + 1);

            return content;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _chatSemaphore?.Dispose();
        }
    }
}