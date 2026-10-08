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
    ///
    /// v1.19.55: the embedding dial. The app default keeps the shape above -
    /// the reader's own embedding model at its resolved embed root. The new
    /// "embed-gemini" choice points the same batch machinery at Google's
    /// OpenAI-compatible /embeddings door with gemini-embedding-2 and the
    /// chat guest's key: same batches, same split-retry, same probe, one
    /// different target.
    /// </summary>
    public sealed class OllamaEmbeddingClient : IDisposable
    {
        /// <summary>Default embedding model; overridable via AiProviderConfig.EmbeddingModel.</summary>
        public const string DefaultModel = "embeddinggemma:latest";

        private readonly Func<AiProviderConfig> _configProvider;
        private readonly HttpClient _httpClient;
        private readonly int _batchSize;

        // Capability probe state (see class doc). A negative probe is retried
        // after EmbeddingRetryCoolDown so a temporarily-down Ollama does not
        // disable semantic search for the whole process run. The state is
        // keyed by (base url, embedding model): changing the model in settings
        // previously kept a stale "available" verdict for a DIFFERENT model.
        private const int CapabilityUnknown = 0;
        private const int CapabilityAvailable = 1;
        private const int CapabilityUnavailable = 2;
        private readonly object _stateLock = new object();
        private string? _capabilityKey;
        private int _capability = CapabilityUnknown;
        private long _unavailableAtUtcTicks;
        private AiErrorCategory _lastCategory = AiErrorCategory.Other;
        private readonly SemaphoreSlim _probeLock = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan EmbeddingRetryCoolDown = TimeSpan.FromSeconds(60);

        // Per-batch deadline (instance, internal-settable for tests): one HTTP
        // call with up to _batchSize inputs must answer within this window or
        // the batch is split and retried - see SendBatchWithSplitRetryAsync.
        internal TimeSpan BatchTimeout { get; set; } = TimeSpan.FromSeconds(60);

        // Splitting floor: the smallest batch that is still WORTH splitting
        // (into a pair). A pair (or single) that blows the deadline is a dead
        // endpoint, not a slow model - fail instead of splitting forever.
        private const int MinSplitBatchSize = 4;

        public OllamaEmbeddingClient(Func<AiProviderConfig> configProvider, int batchSize = 32, HttpMessageHandler? handler = null)
        {
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _batchSize = batchSize > 0 ? batchSize : 32;
            _httpClient = handler is not null
                ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) }
                : new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        // The presets' contract is "embeddings stay on the local Ollama
        // bridge" (the NVIDIA NIM preset's own words) - but the embed URL
        // used to ride the CHAT endpoint's host, so a cloud chat config
        // asked integrate.api.nvidia.com (or ollama.com) for /api/embed,
        // took that host's bare 404 for "model missing" and sent readers
        // off to pull embeddinggemma their own Ollama had installed all
        // along. The root is resolved now: a cloud chat host (not loopback,
        // not an Ollama on its native port) never serves the embeddings -
        // the local bridge does. An Ollama on the LAN keeps its own.
        internal const string LocalBridgeRoot = "http://localhost:11434";

        internal static string ResolveEmbedRoot(string? baseUrl)
        {
            var root = baseUrl?.TrimEnd('/') ?? string.Empty;
            if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                root = root[..^3];
            if (root.Length == 0)
                return LocalBridgeRoot;
            if (AiEndpoints.IsLocal(root)
                || root.EndsWith(":11434", StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return LocalBridgeRoot;
        }

        private static string GetEmbedUrl(string baseUrl)
            => ResolveEmbedRoot(baseUrl) + "/api/embed";

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

                var batchResults = await SendBatchWithSplitRetryAsync(batch, cancellationToken).ConfigureAwait(false);
                results.AddRange(batchResults);
            }

            return results.ToArray();
        }

        /// <summary>
        /// Sends one batch under the per-batch deadline. A CPU-only endpoint
        /// can legitimately need longer for a FULL batch than the deadline -
        /// killing the whole pass for that (the 10-minute total deadline did
        /// exactly that) permanently degraded answers to keyword-only. Instead
        /// a timed-out batch is SPLIT and retried as halves: smaller inputs fit
        /// the window, so slow-but-healthy endpoints make progress while a
        /// wedged one fails fast at the floor (no answer even for a tiny batch
        /// within 60s). Caller cancellation is never mistaken for a deadline:
        /// it propagates untouched.
        /// </summary>
        private async Task<List<float[]>> SendBatchWithSplitRetryAsync(List<string> batch, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(BatchTimeout);
            try
            {
                return await GenerateBatchAsync(batch, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && batch.Count >= MinSplitBatchSize)
            {
                int half = batch.Count / 2;
                var first = await SendBatchWithSplitRetryAsync(batch.Take(half).ToList(), ct).ConfigureAwait(false);
                var second = await SendBatchWithSplitRetryAsync(batch.Skip(half).ToList(), ct).ConfigureAwait(false);
                first.AddRange(second);
                return first;
            }
        }

        /// <summary>Where one embedding batch goes: the app default (the
        /// reader's own embedding model at its resolved embed root) or,
        /// when the embedding dial says so, Google's gemini-embedding-2
        /// behind the OpenAI-compatible door (v1.19.55). The dial's URL is
        /// carried verbatim - ResolveEmbedRoot must never see a cloud host
        /// it would bounce to the local bridge.</summary>
        private sealed record EmbedTarget(string Url, string Model, string? ApiKey);

        private static EmbedTarget ResolveTarget(AiProviderConfig config)
        {
            if (AiSurfaceModels.GetEmbeddingChoice() == AiSurfaceModels.EmbeddingGeminiChoice)
            {
                return new EmbedTarget(AiSurfaceModels.GeminiEmbeddingUrl,
                    AiSurfaceModels.GeminiEmbeddingModel, AiSurfaceModels.GoogleGuestApiKey);
            }

            var root = ResolveEmbedRoot(config.BaseUrl);
            var model = string.IsNullOrWhiteSpace(config.EmbeddingModel) ? DefaultModel : config.EmbeddingModel;
            var apiKey = string.IsNullOrWhiteSpace(config.ApiKey) && AiEndpoints.IsLocal(root)
                ? "ollama"
                : config.ApiKey;
            return new EmbedTarget(root + "/api/embed", model, apiKey);
        }

        private async Task<List<float[]>> GenerateBatchAsync(List<string> batch, CancellationToken ct)
        {
            var config = _configProvider();
            var target = ResolveTarget(config);
            var body = new Dictionary<string, object>
            {
                ["model"] = target.Model,
                ["input"] = batch
            };
            var json = JsonSerializer.Serialize(body);
            using var request = new HttpRequestMessage(HttpMethod.Post, target.Url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(target.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.ApiKey);

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var model = target.Model;
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    throw new AiProviderException(AiErrorCategory.ModelNotFound, model, response.StatusCode);
                if (hreLike(response))
                    throw new AiProviderException(AiErrorCategory.OllamaNotRunning, model, null);
                throw new AiProviderException(AiErrorCategory.BadResponse, model, response.StatusCode);

                bool hreLike(HttpResponseMessage r) => (int)r.StatusCode >= 500 || r.StatusCode == System.Net.HttpStatusCode.RequestTimeout;
            }

            return ParseEmbeddings(responseJson, batch.Count);
        }

        /// <summary>
        /// Parses Ollama /api/embed responses:
        ///   {"model":"embeddinggemma:latest","embeddings":[[...],[...]]}
        /// Tolerates the single-input legacy shape {"embedding":[...]} and
        /// the OpenAI-compatible shape {"data":[{"embedding":[...]}]}.
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
            else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                // OpenAI-compatible shape (v1.19.55, the gemini embedding
                // dial): {"data":[{"embedding":[...]}]} in batch order.
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("embedding", out var vec) && vec.ValueKind == JsonValueKind.Array)
                        vectors.Add(ParseVector(vec));
                }
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
            var config = _configProvider();
            var target = ResolveTarget(config);
            var key = CapabilityKey(target);

            lock (_stateLock)
            {
                if (_capabilityKey != key)
                {
                    // Endpoint or model changed: previous verdicts no longer apply.
                    _capabilityKey = key;
                    _capability = CapabilityUnknown;
                }

                if (_capability == CapabilityAvailable)
                    return;

                if (_capability == CapabilityUnavailable)
                {
                    var elapsed = DateTime.UtcNow.Ticks - _unavailableAtUtcTicks;
                    if (elapsed < EmbeddingRetryCoolDown.Ticks)
                    {
                        // A cached MODEL-MISSING verdict must stay typed even on
                        // the fast-fail path: the pass maps it to the localized
                        // "run: ollama pull <model>" message, and the generic
                        // HttpRequestException used to mask it as "unavailable".
                        if (_lastCategory == AiErrorCategory.ModelNotFound)
                            throw new AiProviderException(AiErrorCategory.ModelNotFound, target.Model);
                        throw EmbeddingsUnavailable(target.Model);
                    }
                }
            }

            await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_stateLock)
                {
                    if (_capabilityKey != key)
                    {
                        _capabilityKey = key;
                        _capability = CapabilityUnknown;
                    }
                    if (_capability == CapabilityAvailable)
                        return;
                    if (_capability == CapabilityUnavailable
                        && DateTime.UtcNow.Ticks - _unavailableAtUtcTicks < EmbeddingRetryCoolDown.Ticks)
                    {
                        if (_lastCategory == AiErrorCategory.ModelNotFound)
                            throw new AiProviderException(AiErrorCategory.ModelNotFound, target.Model);
                        throw EmbeddingsUnavailable(target.Model);
                    }
                }

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(ProbeTimeout);
                    await GenerateBatchAsync(new List<string> { "capability probe" }, cts.Token).ConfigureAwait(false);
                    lock (_stateLock)
                    {
                        if (_capability == CapabilityUnavailable)
                            Avalanche.Services.AiHighlightLog.Log(
                                "embedding probe: endpoint recovered - semantic channel re-enabled");
                        _capability = CapabilityAvailable;
                        _lastCategory = AiErrorCategory.Other;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    MarkUnavailable(AiErrorCategory.Timeout);
                    // The probe used to fail silently: the only visible symptom
                    // was a status line stuck on "building" and a semantic
                    // channel that never came back. Log the reason.
                    Avalanche.Services.AiHighlightLog.Log(
                        $"embedding probe: no answer within {ProbeTimeout.TotalSeconds:0}s ({CapabilityKey(target)}) - keyword-only for {EmbeddingRetryCoolDown.TotalSeconds:0}s");
                    throw EmbeddingsUnavailable(target.Model);
                }
                catch (AiProviderException pex)
                {
                    // Typed verdicts (404 model missing, 5xx endpoint trouble)
                    // must reach the caller AS TYPED: wrapping them into a
                    // generic HttpRequestException cost the UI the actionable
                    // "ollama pull <model>" message (regression report: users
                    // only ever saw "semantic search unavailable").
                    MarkUnavailable(pex.Category);
                    Avalanche.Services.AiHighlightLog.Log(
                        $"embedding probe: {pex.Category} ({CapabilityKey(target)}) - keyword-only for {EmbeddingRetryCoolDown.TotalSeconds:0}s");
                    throw;
                }
                catch (Exception ex)
                {
                    MarkUnavailable(AiErrorCategory.OllamaNotRunning);
                    Avalanche.Services.AiHighlightLog.Log(
                        $"embedding probe: unavailable ({ex.GetType().Name}: {Truncate(ex.Message, 160)}) - keyword-only for {EmbeddingRetryCoolDown.TotalSeconds:0}s");
                    throw EmbeddingsUnavailable(target.Model, ex);
                }
            }
            finally
            {
                _probeLock.Release();
            }
        }

        private void MarkUnavailable(AiErrorCategory category)
        {
            lock (_stateLock)
            {
                _unavailableAtUtcTicks = DateTime.UtcNow.Ticks;
                _capability = CapabilityUnavailable;
                _lastCategory = category;
            }
        }

        // Keyed on the resolved embed target - the probe verdict belongs to
        // the server actually asked, not to the chat host that pointed there.
        // The embedding dial rides inside the URL, so flipping the dial
        // naturally invalidates every cached verdict.
        private static string CapabilityKey(EmbedTarget target) =>
            $"{target.Url}|{target.Model}";

        private static HttpRequestException EmbeddingsUnavailable(string model, Exception? inner = null)
        {
            // Log/diagnostic text only - the UI shows localized strings from
            // the view model, never this message.
            return new HttpRequestException(
                $"Embeddings are unavailable ({model}). " +
                $"Search falls back to lexical (BM25) mode until the embedding endpoint answers again.", inner);
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

        public void Dispose()
        {
            _httpClient?.Dispose();
            _probeLock?.Dispose();
        }
    }
}
