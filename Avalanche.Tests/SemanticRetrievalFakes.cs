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

/// <summary>
/// Shared fakes for the semantic retrieval tests: an HttpMessageHandler that
/// serves deterministic /api/embed responses from a text -> vector map, plus
/// helpers to build small document indexes.
/// </summary>
internal sealed class FakeEmbedHandler : HttpMessageHandler
{
    public int RequestCount;
    public readonly List<string> RequestBodies = new();
    public Func<HttpRequestMessage, HttpResponseMessage>? Responder;

    // Async variant (takes precedence over Responder) - lets tests simulate
    // SLOW endpoints by delaying the answer past the client's batch deadline.
    // The CancellationToken MUST be observed by the delay: HttpClient does not
    // force-cancel a custom handler's pending task on its own.
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncResponder;

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RequestCount);
        if (request.Content is not null)
            RequestBodies.Add(request.Content.ReadAsStringAsync(cancellationToken).Result);

        if (AsyncResponder is not null)
            return await AsyncResponder(request, cancellationToken);
        if (Responder is not null)
            return Responder(request);

        // Default: serve deterministic vectors from the map; unknown texts get
        // a distinct unit vector keyed by the text hash.
        var body = JsonSerializer.Deserialize<JsonElement>(RequestBodies[^1]);
        var model = body.GetProperty("model").GetString();
        Assert.Equal("embeddinggemma:latest", model);
        var inputs = body.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
        var vectors = inputs.Select(i => VectorMap.TryGetValue(i, out var v) ? v : UnitVector(i)).ToList();
        var payload = JsonSerializer.Serialize(new { embeddings = vectors });
        return Json(payload);
    }

    public Dictionary<string, float[]> VectorMap { get; } = new();

    private static float[] UnitVector(string text)
    {
        var v = new float[4];
        v[Math.Abs(text.GetHashCode()) % 4] = 1f;
        return v;
    }
}

internal static class SemanticTestHelpers
{
    public const string TerminationText = "The agreement may be terminated after thirty days written notice.";
    public const string LocationText = "The building is located in New York.";
    public const string PaymentText = "The parties must submit payment within ten days.";

    public static DocumentChunk MakeChunk(string documentId, int chunkIndex, int pageNumber, string text)
    {
        return new DocumentChunk
        {
            ChunkId = $"chunk_{documentId}_{chunkIndex}",
            DocumentId = documentId,
            ChunkIndex = chunkIndex,
            Text = text,
            PageIndices = new List<int> { pageNumber - 1 },
            PageSizes = new List<float[]> { new[] { 612f, 792f } },
            PageRotations = new List<int> { 0 },
            CropBoxes = new List<float[]> { new[] { 0f, 0f, 612f, 792f } }
        };
    }

    public static DocumentIndex MakeIndex(string documentId, params (int page, string text)[] chunks)
    {
        var doc = new DocumentIndex
        {
            DocumentId = documentId,
            FilePath = $@"C:\tmp\{documentId}.pdf",
            FileSize = 1234,
            LastWriteTime = 1000,
            ContentHash = "HASH1",
            PageCount = chunks.Length,
            Chunks = chunks.Select((c, i) => MakeChunk(documentId, i, c.page, c.text)).ToList()
        };
        return doc;
    }

    public static VectorIndex NewIndex(string dir, string name)
    {
        return new VectorIndex(System.IO.Path.Combine(dir, $"{name}_{Guid.NewGuid():N}.db"));
    }
}
