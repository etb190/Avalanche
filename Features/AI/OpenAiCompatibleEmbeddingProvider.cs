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
            if (string.IsNullOrWhiteSpace(_config.ApiKey) && !_config.BaseUrl.Contains("localhost"))
                return false;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.BaseUrl.TrimEnd('/')}/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task<float[][]> GenerateBatchAsync(IReadOnlyList<string> texts)
        {
            var requestBody = new
            {
                model = _config.Model,
                input = texts
            };

            var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl.TrimEnd('/')}/embeddings")
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

            var response = await _httpClient.SendAsync(request);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
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
                        var embedding = new float[Dimension];
                        int idx = 0;
                        foreach (var val in embProp.EnumerateArray())
                        {
                            if (idx < Dimension)
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