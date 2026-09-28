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
    /// OpenAI-compatible embedding provider (supports OpenAI, local servers like Ollama, LM Studio, etc.).
    /// </summary>
    public sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider
    {
        private readonly HttpClient _httpClient;
        private readonly EmbeddingProviderConfig _config;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public int Dimension { get; }
        public int MaxTokens { get; }

        public OpenAiCompatibleEmbeddingProvider(EmbeddingProviderConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            Dimension = config.Dimension;
            MaxTokens = config.MaxTokens;
        }

        public async Task<float[][]> GenerateEmbeddingsAsync(IReadOnlyList<string> texts)
        {
            if (texts == null || texts.Count == 0)
                return Array.Empty<float[]>();

            var results = new List<float[]>();

            // Process in batches
            for (int i = 0; i < texts.Count; i += _config.BatchSize)
            {
                var batch = new List<string>();
                int end = Math.Min(i + _config.BatchSize, texts.Count);
                for (int j = i; j < end; j++)
                    batch.Add(texts[j]);

                var batchResults = await GenerateBatchAsync(batch);
                results.AddRange(batchResults);
            }

            return results.ToArray();
        }

        public async Task<float[]> GenerateEmbeddingAsync(string text)
        {
            var results = await GenerateEmbeddingsAsync(new[] { text });
            return results.Length > 0 ? results[0] : Array.Empty<float>();
        }

        public async Task<bool> IsAvailableAsync()
        {
            // For Ollama (localhost), don't require API key
            if (string.IsNullOrWhiteSpace(_config.ApiKey) && !_config.BaseUrl.Contains("localhost"))
                return false;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.BaseUrl.TrimEnd('/')}/models");
                if (!string.IsNullOrWhiteSpace(_config.ApiKey))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public (string ModelName, int Dimension) GetModelInfo()
        {
            return (_config.Model, Dimension);
        }

        private async Task<float[][]> GenerateBatchAsync(IReadOnlyList<string> texts)
        {
            // Apply task prefixes if configured
            var prefixedTexts = new List<string>();
            foreach (var text in texts)
            {
                var prefixed = text;
                // Truncate if too long for embedding model
                if (prefixed.Length > _config.MaxTokens * 4) // rough char estimate
                    prefixed = prefixed.Substring(0, _config.MaxTokens * 4);
                
                // Apply document prefix for indexing
                if (!string.IsNullOrEmpty(_config.DocumentPrefix))
                    prefixed = _config.DocumentPrefix + prefixed;
                
                prefixedTexts.Add(prefixed);
            }

            var requestBody = new
            {
                model = _config.Model,
                input = prefixedTexts
            };

            var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl.TrimEnd('/')}/embeddings")
            {
                Content = content
            };
            if (!string.IsNullOrWhiteSpace(_config.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

            var response = await _httpClient.SendAsync(request);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // Check for model not found error
                if (responseJson.Contains("model not found", StringComparison.OrdinalIgnoreCase) ||
                    responseJson.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException($"Embedding model '{_config.Model}' not found. Run: ollama pull {_config.Model}");
                }
                throw new HttpRequestException($"Embedding API error: {response.StatusCode} - {responseJson}");
            }

            return ParseEmbeddings(responseJson);
        }

        private float[][] ParseEmbeddings(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Invalid embedding response format");

                var results = new List<float[]>();
                foreach (var item in dataProp.EnumerateArray())
                {
                    if (item.TryGetProperty("embedding", out var embProp) && embProp.ValueKind == JsonValueKind.Array)
                    {
                        // Use the actual vector length returned by the server rather
                        // than the configured dimension hint, so models whose
                        // embedding size differs still round-trip consistently
                        // (query and document vectors must have equal length).
                        var embedding = new float[embProp.GetArrayLength()];
                        int idx = 0;
                        foreach (var val in embProp.EnumerateArray())
                        {
                            if (idx < embedding.Length)
                                embedding[idx++] = val.GetSingle();
                        }
                        results.Add(embedding);
                    }
                }

                return results.ToArray();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse embeddings: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}