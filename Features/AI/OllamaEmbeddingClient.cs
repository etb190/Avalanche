using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Ollama-native embedding client for the semantic retrieval layer.
    /// Talks to the NATIVE endpoint POST {root}/api/embed (not the
    /// OpenAI-compatible /v1 one) with the dedicated embedding model
    /// embeddinggemma:latest; the chat model (gpt-oss:120b-cloud) is never
    /// asked for embeddings.
    ///
    /// History lesson (commits c97dbcd/80ea2c1): the previous embedding
    /// pipeline probed a cloud chat model that could not serve embeddings;
    /// stalled batches froze index builds behind "Preparing document...".
    /// This client keeps the proven guards: one tiny capability probe with
    /// a short timeout whose negative result is cached (retried after a
    /// cool-down so a restarted Ollama recovers), a per-batch timeout, and
    /// batched input - one HTTP call per batch, never per chunk.
    /// </summary>
    public sealed class OllamaEmbeddingClient
    {
        /// <summary>Default embedding model; overridable via AiProviderConfig.EmbeddingModel.</summary>
        public const string DefaultModel = "embeddinggemma:latest";

        private readonly Func<AiProviderConfig> _configProvider;
        private readonly HttpClient _httpClient;
        private readonly int _batchSize;

        // Capability probe state (see class doc). A negative probe is retried
        // after EmbeddingRetryCoolDown so a temporarily-down Ollama does not
        // disable semantic search for the whole process run.
        private const int CapabilityUnknown = 0;
        private const int CapabilityAvailable = 1;
        private const int CapabilityUnavailable = 2;
        private int _capability = CapabilityUnknown;
        private long _unavailableAtUtcTicks;
        private readonly SemaphoreSlim _probeLock = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan EmbeddingRetryCoolDown = TimeSpan.FromSeconds(60);

        public OllamaEmbeddingClient(Func<AiProviderConfig> configProvider, int batchSize = 32, HttpMessageHandler? handler = null)
        {
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _batchSize = batchSize > 0 ? batchSize : 32;
            _httpClient = handler is not null
                ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) }
                : new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        private static string GetEmbedUrl(string baseUrl)
        {
            // Chat config points at the OpenAI-compatible surface
            // (http://localhost:11434/v1); embeddings live on the native
            // surface. Strip a trailing "/v1" and append /api/embed.
            var root = baseUrl?.TrimEnd('/') ?? "http://localhost:11434";
            if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                root = root[..^3];
            return root + "/api/embed";
        }

        /// <summary>Embeds one text (query or single chunk).</summary>
        public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            var results = await GenerateEmbeddingsAsync(new[] { text }, cancellationToken).ConfigureAwait(false);
            return results.Length > 0 ? results[0] : Array.Empty<float>();
        }

        /// <summary>
        /// Embeds a list of texts in batches. The returned array is aligned
        /// 1:1 with the input; every vector is non-empty and all vectors
        /// share one dimension, otherwise an exception is thrown (callers
        /// fall back to lexical search - never a half-valid result set).
        /// </summary>
        public async Task<float[][]> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            if (texts is null || texts.Count == 0)
                return Array.Empty<float[]>();

            await EnsureEmbeddingsAvailableAsync(cancellationToken).ConfigureAwait(false);

            var results = new List<float[]>(texts.Count);
            for (int i = 0; i < texts.Count; i += _batchSize)
            {
                int end = Math.Min(i + _batchSize, texts.Count);
                var batch = new List<string>(end - i);
                for (int j = i; j < end; j++)
                    batch.Add(texts[j] ?? "");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(BatchTimeout);
                var batchResults = await GenerateBatchAsync(batch, cts.Token).ConfigureAwait(false);
                results.AddRange(batchResults);
            }

            return results.ToArray();
        }

        private async Task<List<float[]>> GenerateBatchAsync(List<string> batch, CancellationToken ct)
        {
            var config = _configProvider();
            var body = new Dictionary<string, object>
            {
                ["model"] = string.IsNullOrWhiteSpace(config.EmbeddingModel) ? DefaultModel : config.EmbeddingModel,
                ["input"] = batch
            };
            var json = JsonSerializer.Serialize(body);
            using var request = new HttpRequestMessage(HttpMethod.Post, GetEmbedUrl(config.BaseUrl))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            var apiKey = string.IsNullOrWhiteSpace(config.ApiKey) && (config.BaseUrl?.Contains("localhost") ?? false)
                ? "ollama"
                : config.ApiKey;
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Ollama embed failed: {(int)response.StatusCode} {response.StatusCode} - {Truncate(responseJson, 300)}");

            return ParseEmbeddings(responseJson, batch.Count);
        }

        /// <summary>
        /// Parses Ollama /api/embed responses:
        ///   {"model":"embeddinggemma:latest","embeddings":[[...],[...]]}
        /// Tolerates the single-input legacy shape {"embedding":[...]}.
        /// Throws on malformed responses, wrong count or empty vectors -
        /// callers must never store half an embedding batch.
        /// </summary>
        public static List<float[]> ParseEmbeddings(string responseJson, int expectedCount)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
                throw new InvalidOperationException("Ollama embed returned an empty response.");

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            List<float[]> vectors = new();
            if (root.TryGetProperty("embeddings", out var embeddings) && embeddings.ValueKind == JsonValueKind.Array)
            {
                foreach (var vec in embeddings.EnumerateArray())
                    vectors.Add(ParseVector(vec));
            }
            else if (root.TryGetProperty("embedding", out var single) && single.ValueKind == JsonValueKind.Array)
            {
                vectors.Add(ParseVector(single));
            }
            else
            {
                throw new InvalidOperationException("Ollama embed response has no 'embeddings' array.");
            }

            if (vectors.Count != expectedCount)
                throw new InvalidOperationException(
                    $"Ollama embed returned {vectors.Count} vector(s) for {expectedCount} input(s).");

            return vectors;
        }

        private static float[] ParseVector(JsonElement vec)
        {
            if (vec.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Ollama embed vector is not an array.");

            var values = new List<float>(vec.GetArrayLength());
            foreach (var v in vec.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Number)
                    throw new InvalidOperationException("Ollama embed vector contains a non-numeric value.");
                values.Add(v.GetSingle());
            }

            if (values.Count == 0)
                throw new InvalidOperationException("Ollama embed returned an empty vector.");

            return values.ToArray();
        }

        /// <summary>
        /// One tiny probe before the first real use; a negative result is
        /// cached for EmbeddingRetryCoolDown so every later call fails fast
        /// (retrieval falls back to lexical instantly) but a restarted
        /// Ollama recovers within a minute.
        /// </summary>
        private async Task EnsureEmbeddingsAvailableAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _capability) == CapabilityAvailable)
                return;

            if (Volatile.Read(ref _capability) == CapabilityUnavailable)
            {
                var elapsed = DateTime.UtcNow.Ticks - Volatile.Read(ref _unavailableAtUtcTicks);
                if (elapsed < EmbeddingRetryCoolDown.Ticks)
                    throw EmbeddingsUnavailable();
            }

            await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _capability) == CapabilityAvailable)
                    return;
                if (Volatile.Read(ref _capability) == CapabilityUnavailable
                    && DateTime.UtcNow.Ticks - Volatile.Read(ref _unavailableAtUtcTicks) < EmbeddingRetryCoolDown.Ticks)
                    throw EmbeddingsUnavailable();

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(ProbeTimeout);
                    await GenerateBatchAsync(new List<string> { "capability probe" }, cts.Token).ConfigureAwait(false);
                    Volatile.Write(ref _capability, CapabilityAvailable);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    MarkUnavailable();
                    throw EmbeddingsUnavailable();
                }
                catch (Exception ex)
                {
                    MarkUnavailable();
                    throw EmbeddingsUnavailable(ex);
                }
            }
            finally
            {
                _probeLock.Release();
            }
        }

        private void MarkUnavailable()
        {
            Volatile.Write(ref _unavailableAtUtcTicks, DateTime.UtcNow.Ticks);
            Volatile.Write(ref _capability, CapabilityUnavailable);
        }

        private static HttpRequestException EmbeddingsUnavailable(Exception? inner = null) =>
            new HttpRequestException(
                $"Embeddings are unavailable (embeddinggemma:latest at the local Ollama bridge). " +
                $"Search falls back to lexical (BM25) mode until Ollama serves /api/embed.", inner);

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

        public void Dispose()
        {
            _httpClient?.Dispose();
            _probeLock?.Dispose();
        }
    }
}
