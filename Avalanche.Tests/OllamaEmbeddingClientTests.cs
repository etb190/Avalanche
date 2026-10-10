using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalanche.Features.AI;
using Xunit;

namespace Avalanche.Tests;

public sealed class OllamaEmbeddingClientTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "avalanche_embtests_" + Guid.NewGuid().ToString("N"));

    public OllamaEmbeddingClientTests() => System.IO.Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { }
    }

    // v1.19.93: the machinery tests (batching, deadlines, probes) speak to a
    // LOCAL endpoint - the old default config was the NIM cloud host, which
    // now serves its own embeddings instead of bouncing to the bridge.
    private OllamaEmbeddingClient NewClient(FakeEmbedHandler handler, int batchSize = 32)
        => new(() => new AiProviderConfig { BaseUrl = "http://localhost:11434/v1" }, batchSize, handler);

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void Parse_MultiInputResponse_ReturnsAlignedVectors()
    {
        const string json = "{\"model\":\"embeddinggemma:latest\",\"embeddings\":[[1,0,0.5],[0,2,0]]}";
        var vectors = OllamaEmbeddingClient.ParseEmbeddings(json, 2);

        Assert.Equal(2, vectors.Count);
        Assert.Equal(3, vectors[0].Length);
        Assert.Equal(1f, vectors[0][0]);
        Assert.Equal(0.5f, vectors[0][2]);
        Assert.Equal(2f, vectors[1][1]);
    }

    [Fact]
    public void Parse_SingleLegacyShape_IsTolerated()
    {
        const string json = "{\"embedding\":[0.25,0.75]}";
        var vectors = OllamaEmbeddingClient.ParseEmbeddings(json, 1);
        Assert.Single(vectors);
        Assert.Equal(0.75f, vectors[0][1]);
    }

    [Fact]
    public void Parse_OpenAiDataShape_IsTolerated()
    {
        // v1.19.55: the gemini embedding dial answers in the OpenAI-compatible
        // shape - batch order rides the data array's index.
        const string json = "{\"data\":[{\"index\":0,\"embedding\":[1,0.5]},{\"index\":1,\"embedding\":[0,2]}]}";
        var vectors = OllamaEmbeddingClient.ParseEmbeddings(json, 2);
        Assert.Equal(2, vectors.Count);
        Assert.Equal(1f, vectors[0][0]);
        Assert.Equal(0.5f, vectors[0][1]);
        Assert.Equal(2f, vectors[1][1]);
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        Assert.ThrowsAny<Exception>(() => OllamaEmbeddingClient.ParseEmbeddings("{not json", 1));
    }

    [Fact]
    public void Parse_EmptyResponseText_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => OllamaEmbeddingClient.ParseEmbeddings("", 1));
    }

    [Fact]
    public void Parse_VectorCountMismatch_Throws()
    {
        const string json = "{\"embeddings\":[[1,0],[0,1]]}";
        Assert.Throws<InvalidOperationException>(() => OllamaEmbeddingClient.ParseEmbeddings(json, 3));
    }

    [Fact]
    public void Parse_EmptyVector_Throws()
    {
        const string json = "{\"embeddings\":[[]]}";
        Assert.Throws<InvalidOperationException>(() => OllamaEmbeddingClient.ParseEmbeddings(json, 1));
    }

    [Fact]
    public void Parse_NonNumericValue_Throws()
    {
        const string json = "{\"embeddings\":[[1,\"x\"]]}";
        Assert.Throws<InvalidOperationException>(() => OllamaEmbeddingClient.ParseEmbeddings(json, 1));
    }

    [Fact]
    public async Task GenerateEmbeddings_BatchesInput_KeepsOrderAndAlignment()
    {
        var handler = new FakeEmbedHandler();
        var c = NewClient(handler, batchSize: 32);
        var texts = Enumerable.Range(0, 70).Select(i => $"text {i}").ToList();
        var vectors = await c.GenerateEmbeddingsAsync(texts);

        // 70 texts / batch 32 -> 3 embed calls + 1 capability probe
        Assert.Equal(4, handler.RequestCount);
        Assert.Equal(70, vectors.Length);
        for (int i = 0; i < texts.Count; i++)
            Assert.Equal(4, vectors[i].Length);
        c.Dispose();
    }

    [Fact]
    public async Task GenerateEmbeddings_UsesEmbedModelAndNativeEndpoint()
    {
        var handler = new FakeEmbedHandler();
        var c = NewClient(handler, batchSize: 4);
        await c.GenerateEmbeddingAsync("hello");
        c.Dispose();

        // The model name must reach the request body.
        Assert.Contains("embeddinggemma:latest", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task GenerateEmbeddings_PostsToApiEmbed()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeEmbedHandler
        {
            Responder = r =>
            {
                seen = r;
                return Json("{\"embeddings\":[[1,0,0,0]]}");
            }
        };
        var c = NewClient(handler, batchSize: 4);
        await c.GenerateEmbeddingAsync("q");
        c.Dispose();

        Assert.NotNull(seen);
        Assert.EndsWith("/api/embed", seen!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, seen.Method);
    }

    [Fact]
    public async Task GenerateEmbeddings_ServerError_ThrowsTypedProviderException()
    {
        var handler = new FakeEmbedHandler
        {
            Responder = _ => Json("{\"error\":\"boom\"}", HttpStatusCode.InternalServerError)
        };
        var c = NewClient(handler, batchSize: 4);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => c.GenerateEmbeddingAsync("q"));
        c.Dispose();

        // Typed verdicts must survive: the UI maps the category to the
        // localized message instead of a generic "unavailable" line.
        Assert.Equal(AiErrorCategory.OllamaNotRunning, ex.Category);
    }

    [Fact]
    public async Task Probe_ModelMissing404_IsTypedAndCached_WithinCoolDown()
    {
        var handler = new FakeEmbedHandler
        {
            Responder = _ => Json("{\"error\":\"model missing\"}", HttpStatusCode.NotFound)
        };
        var c = NewClient(handler, batchSize: 4);

        // 404 = model absent: BOTH the live probe AND the cool-down fast-fail
        // must carry the typed verdict, otherwise the UI can only say
        // "unavailable" instead of the actionable "run: ollama pull <model>".
        var ex1 = await Assert.ThrowsAsync<AiProviderException>(() => c.GenerateEmbeddingAsync("q"));
        var ex2 = await Assert.ThrowsAsync<AiProviderException>(() => c.GenerateEmbeddingAsync("q"));
        c.Dispose();

        Assert.Equal(AiErrorCategory.ModelNotFound, ex1.Category);
        Assert.Equal(AiErrorCategory.ModelNotFound, ex2.Category);
        // One probe only: the negative capability is cached (fast-fail), the
        // second call never touched the network.
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Probe_Unreachable_WrapsAsUnavailable()
    {
        var handler = new FakeEmbedHandler
        {
            Responder = _ => throw new HttpRequestException("connection refused")
        };
        var c = NewClient(handler, batchSize: 4);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => c.GenerateEmbeddingAsync("q"));
        c.Dispose();

        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatchDeadline_SplitsBatch_AsHalvesAndCompletes()
    {
        // A CPU-only endpoint can need longer for a FULL batch than the
        // per-batch deadline. The batch is split and retried as halves so the
        // pass makes progress instead of dying (the old 10-minute total
        // deadline killed exactly these healthy-but-slow passes).
        var handler = new FakeEmbedHandler
        {
            AsyncResponder = async (r, ct) =>
            {
                var body = await r.Content!.ReadAsStringAsync(ct);
                var doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);
                int count = doc.GetProperty("input").GetArrayLength();
                var vectors = Enumerable.Range(0, count).Select(_ => new float[] { 1f, 0f, 0f, 0f }).ToArray();
                var payload = System.Text.Json.JsonSerializer.Serialize(new { embeddings = vectors });
                if (count > 2)
                    await Task.Delay(1000, ct); // slower than the batch deadline (token observed!)
                return Json(payload);
            }
        };
        var c = NewClient(handler, batchSize: 8);
        c.BatchTimeout = TimeSpan.FromMilliseconds(300);

        var texts = Enumerable.Range(0, 8).Select(i => $"text {i}").ToList();
        var vectors = await c.GenerateEmbeddingsAsync(texts);
        c.Dispose();

        // 8 inputs -> one deadline-violating batch of 8, split 4+4 (still too
        // slow), each split again into pairs (fast). Aligned result.
        Assert.Equal(8, vectors.Length);
        Assert.All(vectors, v => Assert.Equal(4, v.Length));
        Assert.Equal(1 + 1 + 2 + 4, handler.RequestCount); // probe + 1 + 2 + 4
    }

    [Fact]
    public async Task BatchDeadline_AtSplitFloor_PropagatesCancellation()
    {
        // A batch that cannot be split further (a pair) that STILL blows the
        // deadline is a dead endpoint, not a slow model: the cancellation
        // propagates (the pass then ends in the visible keyword-only state)
        // instead of splitting down to single items forever.
        var handler = new FakeEmbedHandler
        {
            AsyncResponder = async (r, ct) =>
            {
                var body = await r.Content!.ReadAsStringAsync(ct);
                var doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);
                int count = doc.GetProperty("input").GetArrayLength();
                var vectors = Enumerable.Range(0, count).Select(_ => new float[] { 1f, 0f, 0f, 0f }).ToArray();
                var payload = System.Text.Json.JsonSerializer.Serialize(new { embeddings = vectors });
                if (count >= 2)
                    await Task.Delay(1000, ct); // pairs stall too: nothing to split into
                return Json(payload);
            }
        };
        var c = NewClient(handler, batchSize: 4);
        c.BatchTimeout = TimeSpan.FromMilliseconds(300);

        var texts = new List<string> { "a", "b" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GenerateEmbeddingsAsync(texts));
        c.Dispose();
    }

    [Fact]
    public async Task MappedVectors_FlowThroughEndToEnd()
    {
        var handler = new FakeEmbedHandler();
        handler.VectorMap["termination"] = new[] { 1f, 0f, 0f, 0f };
        var c = NewClient(handler, batchSize: 4);
        var v = await c.GenerateEmbeddingAsync("termination");
        c.Dispose();

        Assert.Equal(new[] { 1f, 0f, 0f, 0f }, v);
    }

    // ---- v1.19.93: a cloud chat endpoint serves its OWN embeddings ----
    // (v1.19.27's "rides the local Ollama bridge" is revoked: cloud users
    // get semantic search back - the OpenAI-compatible {base}/embeddings
    // door with the host's own key, and a dead door degrades to lexical
    // exactly as a dead Ollama always did.)

    [Fact]
    public async Task GenerateEmbeddings_CloudChatEndpoint_ServesItsOwnEmbeddings()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeEmbedHandler
        {
            Responder = r =>
            {
                seen = r;
                return Json("{\"embeddings\":[[1,0,0,0]]}");
            }
        };
        var c = new OllamaEmbeddingClient(() => new AiProviderConfig
        {
            BaseUrl = "https://integrate.api.nvidia.com/v1",
            ApiKey = "nvapi-secret",
            EmbeddingModel = "text-embedding-3-small"
        }, 4, handler);
        await c.GenerateEmbeddingAsync("q");
        c.Dispose();

        Assert.NotNull(seen);
        Assert.Equal("https://integrate.api.nvidia.com/v1/embeddings", seen!.RequestUri!.ToString());
        Assert.Contains("text-embedding-3-small", handler.RequestBodies[0]);
        Assert.Equal("Bearer nvapi-secret", seen.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task GenerateEmbeddings_OllamaComChatEndpoint_ServesItsOwnEmbeddings()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeEmbedHandler
        {
            Responder = r =>
            {
                seen = r;
                return Json("{\"embeddings\":[[1,0,0,0]]}");
            }
        };
        var c = new OllamaEmbeddingClient(() => new AiProviderConfig
        {
            BaseUrl = "https://ollama.com/v1",
            ApiKey = "k"
        }, 4, handler);
        await c.GenerateEmbeddingAsync("q");
        c.Dispose();

        Assert.Equal("https://ollama.com/v1/embeddings", seen!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GenerateEmbeddings_LocalChatEndpoint_KeepsItsOwnBridge()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeEmbedHandler
        {
            Responder = r =>
            {
                seen = r;
                return Json("{\"embeddings\":[[1,0,0,0]]}");
            }
        };
        var c = new OllamaEmbeddingClient(() => new AiProviderConfig
        {
            BaseUrl = "http://localhost:11434/v1",
            ApiKey = "ollama",
            EmbeddingModel = "embeddinggemma:latest"
        }, 4, handler);
        await c.GenerateEmbeddingAsync("q");
        c.Dispose();

        Assert.Equal("http://localhost:11434/api/embed", seen!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434")]
    [InlineData("http://127.0.0.1:11434/v1", "http://127.0.0.1:11434")]
    [InlineData("http://192.168.1.50:11434/v1", "http://192.168.1.50:11434")]
    [InlineData("http://localhost:11434", "http://localhost:11434")]
    [InlineData(null, "http://localhost:11434")]
    [InlineData("https://integrate.api.nvidia.com/v1", "http://localhost:11434")]
    [InlineData("https://integrate.api.nvidia.com", "http://localhost:11434")]
    [InlineData("https://ollama.com/v1", "http://localhost:11434")]
    public void ResolveEmbedRoot_FollowsTheServerThatServesEmbeddings(string? baseUrl, string expected)
        => Assert.Equal(expected, OllamaEmbeddingClient.ResolveEmbedRoot(baseUrl));
}
