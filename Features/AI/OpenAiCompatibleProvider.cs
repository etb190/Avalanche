using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// OpenAI-compatible API provider (supports OpenAI, local servers like Ollama, LM Studio, etc.).
    /// </summary>
    public sealed class OpenAiCompatibleProvider : IAiProvider
    {
        private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

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
            var requestBody = BuildRequest(systemPrompt, messages, contextChunks, sourceReferences, config);

            var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.BaseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

            var response = await _httpClient.SendAsync(request);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"AI API error: {response.StatusCode} - {responseJson}");
            }

            return ParseResponse(responseJson);
        }

        public async Task<bool> IsAvailableAsync(AiProviderConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.ApiKey) && !config.BaseUrl.Contains("localhost"))
                return false;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.BaseUrl.TrimEnd('/')}/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
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
            List<DocumentChunk> contextChunks,
            string sourceReferences,
            AiProviderConfig config)
        {
            var fullSystemPrompt = systemPrompt + "\n\n" + sourceReferences;

            var requestMessages = new List<object>
            {
                new { role = "system", content = fullSystemPrompt }
            };

            // Add conversation history (limit to last N messages to control token usage)
            var recentMessages = messages.Where(m => m.MessageRole != ChatMessage.Role.System).TakeLast(10);
            foreach (var msg in recentMessages)
            {
                requestMessages.Add(new { role = msg.MessageRole.ToString().ToLowerInvariant(), content = msg.Content });
            }

            return new
            {
                model = config.Model,
                messages = requestMessages,
                temperature = config.Temperature,
                max_tokens = config.MaxTokens,
                response_format = new { type = "json_object" }
            };
        }

        private AiResponse ParseResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var choice = root.GetProperty("choices")[0];
                var message = choice.GetProperty("message");
                var content = message.GetProperty("content").GetString() ?? "";

                return ParseStructuredContent(content);
            }
            catch
            {
                // Fallback: treat entire response as answer
                return new AiResponse { Answer = json, Sources = new List<AiSource>() };
            }
        }

        private AiResponse ParseStructuredContent(string content)
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
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
                return new AiResponse { Answer = content, Sources = new List<AiSource>() };
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}