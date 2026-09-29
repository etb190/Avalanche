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

    private OllamaEmbeddingClient NewClient(FakeEmbedHandler handler, int batchSize = 32)
        => new(() => new AiProviderConfig(), batchSize, handler);

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
    public async Task GenerateEmbeddings_ServerError_ThrowsHttpRequestException()
    {
        var handler = new FakeEmbedHandler
        {
            Responder = _ => Json("{\"error\":\"boom\"}", HttpStatusCode.InternalServerError)
        };
        var c = NewClient(handler, batchSize: 4);
        await Assert.ThrowsAsync<HttpRequestException>(() => c.GenerateEmbeddingAsync("q"));
        c.Dispose();
    }

    [Fact]
    public async Task ProbeFailure_IsCached_WithinCoolDown()
    {
        var handler = new FakeEmbedHandler
        {
            Responder = _ => Json("{\"error\":\"model missing\"}", HttpStatusCode.NotFound)
        };
        var c = NewClient(handler, batchSize: 4);

        var ex1 = await Assert.ThrowsAsync<HttpRequestException>(() => c.GenerateEmbeddingAsync("q"));
        var ex2 = await Assert.ThrowsAsync<HttpRequestException>(() => c.GenerateEmbeddingAsync("q"));
        c.Dispose();

        Assert.Contains("unavailable", ex1.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable", ex2.Message, StringComparison.OrdinalIgnoreCase);
        // One probe only: the negative capability is cached (fast-fail), the
        // second call never touched the network.
        Assert.Equal(1, handler.RequestCount);
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
}
