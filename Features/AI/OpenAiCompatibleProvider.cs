using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// OpenAI-compatible API provider (supports OpenAI, local servers like Ollama, LM Studio, etc.).
    ///
    /// Error contract: every failure is thrown as <see cref="AiProviderException"/>
    /// with a machine-readable category - callers map the category to localized
    /// text and never see response bodies or raw exception messages. Parsing is
    /// type-tolerant (models drift), and the optional request fields
    /// (response_format / reasoning_effort) a server rejected are remembered per
    /// endpoint so the drop is learned once, not re-paid as a failed round trip
    /// on every message against a queue-limited cloud account.
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

        // Learned optional-field drops per endpoint (provider|base url|model):
        // after a 400 that names one of the optional fields, the field is
        // dropped from every later request for that endpoint instead of
        // re-failing one round trip per turn.
        private static readonly ConcurrentDictionary<string, (bool JsonDropped, bool ReasoningDropped)> LearnedFieldDrops = new();

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
                bool jsonOutput = config.RequestJsonOutput && !LearnedJsonDrop(config);
                string? reasoning = string.IsNullOrWhiteSpace(config.ReasoningEffort) ? null : config.ReasoningEffort;
                if (reasoning is not null && LearnedReasoningDrop(config))
                    reasoning = null;

                // Each loop iteration builds a fresh request; the only way back
                // around the loop is a 400 that explicitly names an optional
                // field, and each field is dropped at most once, so this is
                // bounded to three attempts.
                while (true)
                {
                    var body = BuildRequestBody(systemPrompt, messages, sourceReferences, config, jsonOutput, reasoning);
                    var response = await SendWithRetryAsync(() => CreateRequest(body, config), config);
                    var responseJson = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        // Strict OpenAI-compatible servers reject unknown request
                        // fields. If the 400 names one of the optional ones, drop
                        // it and retry once instead of failing the whole turn -
                        // and remember the drop so later turns never pay it again.
                        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                        {
                            if (jsonOutput && responseJson.Contains("response_format", StringComparison.OrdinalIgnoreCase))
                            {
                                RememberJsonDrop(config);
                                jsonOutput = false;
                                response.Dispose();
                                continue;
                            }
                            if (reasoning is not null && responseJson.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase))
                            {
                                RememberReasoningDrop(config);
                                reasoning = null;
                                response.Dispose();
                                continue;
                            }
                        }

                        throw MapError(response.StatusCode, responseJson, config);
                    }

                    return ParseResponse(responseJson);
                }
            }
            finally
            {
                _chatSemaphore.Release();
            }
        }

        private static string EndpointKey(AiProviderConfig config) =>
            $"{config.ProviderType}|{config.BaseUrl}|{config.Model}";

        private static bool LearnedJsonDrop(AiProviderConfig config) =>
            LearnedFieldDrops.TryGetValue(EndpointKey(config), out var v) && v.JsonDropped;

        private static bool LearnedReasoningDrop(AiProviderConfig config) =>
            LearnedFieldDrops.TryGetValue(EndpointKey(config), out var v) && v.ReasoningDropped;

        private static void RememberJsonDrop(AiProviderConfig config) =>
            LearnedFieldDrops.AddOrUpdate(EndpointKey(config), (true, false), (_, v) => (true, v.ReasoningDropped));

        private static void RememberReasoningDrop(AiProviderConfig config) =>
            LearnedFieldDrops.AddOrUpdate(EndpointKey(config), (false, true), (_, v) => (v.JsonDropped, true));

        /// <summary>Builds a fresh HttpRequestMessage. HttpContent cannot be
        /// reused across sends, so this factory runs once per attempt.</summary>
        private HttpRequestMessage CreateRequest(Dictionary<string, object> body, AiProviderConfig config)
        {
            var json = JsonSerializer.Serialize(body, _jsonOptions);
            var request = new HttpRequestMessage(HttpMethod.Post, $"{config.BaseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            // For Ollama (localhost), use dummy key if empty
            var apiKey = string.IsNullOrWhiteSpace(config.ApiKey) && AiEndpoints.IsLocal(config.BaseUrl)
                ? "ollama"
                : config.ApiKey;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return request;
        }

        /// <summary>
        /// Sends the request. Retries at most ONCE, and only on 429/503 (the
        /// statuses that map to the Busy category) - a 429 whose body names a
        /// usage/credit limit is a hard stop (UsageLimit) and never retried.
        /// Connection failures and HttpClient timeouts are classified into
        /// typed categories here, where they surface.
        /// </summary>
        private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, AiProviderConfig config)
        {
            for (int attempt = 0; ; attempt++)
            {
                using var request = requestFactory();
                HttpResponseMessage response;
                string? busyBody = null;
                try
                {
                    response = await _httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode && IsBusyStatus(response.StatusCode))
                        busyBody = await response.Content.ReadAsStringAsync();
                }
                catch (HttpRequestException hre) when (IsConnectionFailure(hre))
                {
                    throw new AiProviderException(AiErrorCategory.OllamaNotRunning, config.Model, null, hre);
                }
                catch (OperationCanceledException oce)
                {
                    // HttpClient timeout surfaces as TaskCanceledException; with
                    // no caller cancellation yet (C1 adds the token), any OCE
                    // here is the 5-minute client timeout.
                    throw new AiProviderException(AiErrorCategory.Timeout, config.Model, null, oce);
                }

                bool retryable = IsBusyStatus(response.StatusCode);
                if (retryable && busyBody is not null && IsUsageLimitBody(busyBody))
                    throw MapError(response.StatusCode, busyBody, config); // hard stop - no retry

                if (!retryable || attempt >= 1)
                    return response;

                // Honor Retry-After when the server sends one; clamp to keep a
                // hostile value from stalling the UI conversation.
                int delayMs = 2000;
                if (response.Headers.RetryAfter?.Delta is { } delta)
                    delayMs = (int)Math.Clamp(delta.TotalMilliseconds, 500, 15000);

                response.Dispose();
                await Task.Delay(delayMs);
            }
        }

        private static bool IsBusyStatus(System.Net.HttpStatusCode status) =>
            status == System.Net.HttpStatusCode.TooManyRequests
            || status == System.Net.HttpStatusCode.ServiceUnavailable;

        /// <summary>True when a 429 body names an account usage/credit limit
        /// rather than transient queuing. Inspected in memory only - never
        /// echoed to the UI.</summary>
        internal static bool IsUsageLimitBody(string? body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            return body.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
                || body.Contains("usage_limit", StringComparison.OrdinalIgnoreCase)
                || body.Contains("usage-limit", StringComparison.OrdinalIgnoreCase)
                || body.Contains("credit", StringComparison.OrdinalIgnoreCase)
                || body.Contains("quota", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Connection-level failures (refused/unreachable) map to
        /// OllamaNotRunning. On Windows the message reads "No connection could
        /// be made because the target machine actively refused it" - text
        /// matching is unreliable, so this inspects the typed
        /// HttpRequestError and the innermost SocketException instead.</summary>
        internal static bool IsConnectionFailure(HttpRequestException hre)
        {
            var err = hre.HttpRequestError;
            if (err == HttpRequestError.ConnectionError
                || err == HttpRequestError.NameResolutionError)
                return true;

            for (Exception? inner = hre; inner is not null; inner = inner.InnerException)
            {
                if (inner is SocketException se
                    && (se.SocketErrorCode == SocketError.ConnectionRefused
                        || se.SocketErrorCode == SocketError.HostUnreachable
                        || se.SocketErrorCode == SocketError.HostNotFound))
                    return true;
            }
            return false;
        }

        public async Task<bool> IsAvailableAsync(AiProviderConfig config)
        {
            // For Ollama (localhost), don't require API key
            var apiKey = string.IsNullOrWhiteSpace(config.ApiKey) && AiEndpoints.IsLocal(config.BaseUrl)
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

        /// <summary>
        /// Builds the request body. response_format and reasoning_effort are
        /// optional: gpt-oss wants both, but other OpenAI-compatible models
        /// (Nemotron and friends) may reject unknown fields, so they are only
        /// sent when enabled in the configuration.
        /// </summary>
        private Dictionary<string, object> BuildRequestBody(
            string systemPrompt,
            List<ChatMessage> messages,
            string sourceReferences,
            AiProviderConfig config,
            bool jsonOutput,
            string? reasoningEffort)
        {
            var fullSystemPrompt = string.IsNullOrEmpty(sourceReferences)
                ? systemPrompt
                : systemPrompt + "\n\n" + sourceReferences;

            var requestMessages = new List<object>
            {
                new { role = "system", content = fullSystemPrompt }
            };

            // History is capped by the view model (single configurable cap);
            // the provider no longer applies a second, hidden trim.
            var recentMessages = messages.Where(m => m.MessageRole != ChatMessage.Role.System);
            foreach (var msg in recentMessages)
            {
                requestMessages.Add(new { role = msg.MessageRole.ToString().ToLowerInvariant(), content = msg.Content });
            }

            var body = new Dictionary<string, object>
            {
                ["model"] = config.Model,
                ["messages"] = requestMessages,
                ["temperature"] = config.Temperature,
                ["max_tokens"] = config.MaxTokens,
                ["top_p"] = config.TopP
            };

            if (jsonOutput)
                body["response_format"] = new { type = "json_object" };
            if (reasoningEffort is not null)
                body["reasoning_effort"] = reasoningEffort;

            return body;
        }

        /// <summary>
        /// Parses the chat completion envelope. Throws typed failures:
        /// CutOff when finish_reason == "length" (the model ran out of
        /// tokens - even salvageable text is flagged so the answer is shown
        /// as an error and stays out of chat history), BadResponse when the
        /// envelope has no usable content. The raw JSON is never returned
        /// as the answer.
        /// </summary>
        internal static AiResponse ParseResponse(string json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException jex)
            {
                throw new AiProviderException(AiErrorCategory.BadResponse, inner: jex);
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0
                    || !choices[0].TryGetProperty("message", out var message))
                {
                    throw new AiProviderException(AiErrorCategory.BadResponse);
                }

                var choice = choices[0];

                // Reasoning field (gpt-oss): intentionally ignored for display.
                if (message.TryGetProperty("reasoning", out var reasoningProp)
                    && reasoningProp.ValueKind == JsonValueKind.String)
                {
                    System.Diagnostics.Debug.WriteLine($"[AI Reasoning]: {reasoningProp.GetString()}");
                }

                var content = message.TryGetProperty("content", out var contentProp)
                              && contentProp.ValueKind == JsonValueKind.String
                    ? contentProp.GetString() ?? ""
                    : "";

                var finishReason = choice.TryGetProperty("finish_reason", out var frProp) && frProp.ValueKind == JsonValueKind.String
                    ? frProp.GetString()
                    : null;

                if (finishReason == "length")
                    throw new AiProviderException(AiErrorCategory.CutOff);

                if (string.IsNullOrEmpty(content))
                    throw new AiProviderException(AiErrorCategory.BadResponse);

                return ParseStructuredContent(content);
            }
        }

        /// <summary>
        /// Parses the model's structured reply. Every field access is
        /// type-tolerant (sourceId may arrive as a string OR a number,
        /// quote/reason may be missing); "page"/"pageIndex" are NOT read -
        /// the app derives pages from sourceId, and a string-typed "page"
        /// used to crash the parse and leak the whole JSON as the answer.
        /// When no JSON object with an "answer" is found, the raw text is
        /// shown as a plain answer without sources.
        /// </summary>
        internal static AiResponse ParseStructuredContent(string content)
        {
            var jsonContent = ExtractJsonFromMarkdown(content);
            if (jsonContent is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(jsonContent);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object
                        && (root.TryGetProperty("answer", out _) || root.TryGetProperty("sources", out _)))
                    {
                        var answer = GetTolerantString(root, "answer") ?? "";
                        var sources = new List<AiSource>();

                        if (root.TryGetProperty("sources", out var sourcesProp) && sourcesProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var src in sourcesProp.EnumerateArray())
                            {
                                if (src.ValueKind != JsonValueKind.Object) continue;
                                sources.Add(new AiSource
                                {
                                    SourceId = GetTolerantString(src, "sourceId") ?? "",
                                    Quote = GetTolerantString(src, "quote") ?? "",
                                    Reason = GetTolerantString(src, "reason") ?? ""
                                });
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(answer))
                            return new AiResponse { Answer = answer, Sources = sources };
                    }
                }
                catch (JsonException)
                {
                    // not JSON after all - fall through to the plain-text answer
                }
            }

            return new AiResponse { Answer = content.Trim(), Sources = new List<AiSource>() };
        }

        /// <summary>Reads a property as a string whether the model sent a
        /// JSON string or a number; null when missing or neither.</summary>
        private static string? GetTolerantString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var prop)) return null;
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.GetRawText(),
                _ => null
            };
        }

        /// <summary>
        /// Extracts a JSON object from a model reply that may wrap it in
        /// markdown fences or surround it with prose. Instead of the old
        /// first-'{'-to-last-'}' slice (which broke on prose containing
        /// braces), this scans for the first BALANCED top-level object -
        /// brace depth tracking with string/escape awareness - and parses
        /// candidates until one is valid.
        /// </summary>
        internal static string? ExtractJsonFromMarkdown(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;

            // Fenced blocks first (```json ... ```): scan inside each fence.
            var fenced = Regex.Matches(content, @"```(?:json)?\s*(.*?)\s*```", RegexOptions.Singleline);
            foreach (Match m in fenced)
            {
                var candidate = FirstBalancedObject(m.Groups[1].Value);
                if (candidate is not null) return candidate;
            }

            return FirstBalancedObject(content);
        }

        private static string? FirstBalancedObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int depth = 0, start = -1;
            bool inString = false, escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; }
                else if (ch == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (ch == '}')
                {
                    if (depth == 0) continue;
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        var candidate = text.Substring(start, i - start + 1);
                        try
                        {
                            using var probe = JsonDocument.Parse(candidate);
                            return candidate;
                        }
                        catch (JsonException)
                        {
                            start = -1; // keep scanning for a later balanced object
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>Maps an HTTP status (plus the in-memory body where the
        /// caller already has it) to a typed failure. Response bodies are
        /// inspected for the usage-limit signal but never embedded in the
        /// exception message.</summary>
        private static AiProviderException MapError(System.Net.HttpStatusCode statusCode, string responseJson, AiProviderConfig config)
        {
            return statusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                    new AiProviderException(AiErrorCategory.NotSignedIn, config.Model, statusCode),

                System.Net.HttpStatusCode.NotFound =>
                    new AiProviderException(AiErrorCategory.ModelNotFound, config.Model, statusCode),

                System.Net.HttpStatusCode.TooManyRequests => IsUsageLimitBody(responseJson)
                    ? new AiProviderException(AiErrorCategory.UsageLimit, config.Model, statusCode)
                    : new AiProviderException(AiErrorCategory.Busy, config.Model, statusCode),

                System.Net.HttpStatusCode.ServiceUnavailable =>
                    new AiProviderException(AiErrorCategory.Busy, config.Model, statusCode),

                _ => new AiProviderException(AiErrorCategory.Other, config.Model, statusCode)
            };
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _chatSemaphore?.Dispose();
        }
    }
}
