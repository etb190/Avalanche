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
    internal sealed class OpenAiCompatibleProvider : IAiProvider
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
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
            var requestBody = BuildRequest(systemPrompt, messages, contextChunks, config);

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
            AiProviderConfig config)
        {
            var contextText = BuildContextText(contextChunks);
            var fullSystemPrompt = systemPrompt + "\n\n" + contextText;

            var requestMessages = new List<object>
            {
                new { role = "system", content = fullSystemPrompt }
            };

            // Add conversation history (limit to last 10 messages to control token usage)
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

        private string BuildContextText(List<DocumentChunk> chunks)
        {
            if (chunks == null || chunks.Count == 0)
                return "No document context available.";

            var sb = new StringBuilder();
            sb.AppendLine("DOCUMENT CONTEXT (relevant passages from the PDF):");
            sb.AppendLine();

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                sb.AppendLine($"[Source {i + 1} - Page {chunk.PageNumber}]");
                sb.AppendLine(chunk.Text);
                sb.AppendLine();
            }

            sb.AppendLine("INSTRUCTIONS:");
            sb.AppendLine("- Answer based ONLY on the provided document context.");
            sb.AppendLine("- If the context doesn't contain the answer, say so clearly.");
            sb.AppendLine("- Provide structured output as JSON with 'answer' and 'sources' fields.");
            sb.AppendLine("- Each source must include: page (number), quote (exact text from context), reason (why this supports the answer).");
            sb.AppendLine("- Use the exact page numbers provided in the context.");

            return sb.ToString();
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
                            PageNumber = src.TryGetProperty("page", out var p) ? p.GetInt32() : 0,
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
    }
}