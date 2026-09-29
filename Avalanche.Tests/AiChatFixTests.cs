using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalanche.Features.AI;
using Avalanche.Services;
using Xunit;

namespace Avalanche.Tests;

// ============================================================
// Group G - regression tests for the AI chat bug-fix pass.
// ============================================================

public sealed class AiProviderParsingTests
{
    private const string Envelope = "{{\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":{0}}},\"finish_reason\":{1}}}]}}";

    private static string EnvelopeWith(string contentJson, string finishReasonJson) =>
        string.Format(Envelope, contentJson, finishReasonJson);

    private static AiResponse ParseContent(string content) =>
        OpenAiCompatibleProvider.ParseResponse(EnvelopeWith(
            System.Text.Json.JsonSerializer.Serialize(content), "stop"));

    [Fact]
    public void FencedJsonWithProse_Parses()
    {
        var response = ParseContent("Here is the answer:\n```json\n{\"answer\":\"Fee doubles.\",\"sources\":[]}\n```");
        Assert.Equal("Fee doubles.", response.Answer);
    }

    [Fact]
    public void ProseWithBraces_BalancedScan_PicksTheJson()
    {
        var response = ParseContent("Note {draft}. {\"answer\":\"Paid on {day}.\",\"sources\":[]} - done.");
        Assert.Equal("Paid on {day}.", response.Answer);
    }

    [Fact]
    public void PlainTextAnswer_NoSources()
    {
        var response = ParseContent("The trial lasted twelve weeks.");
        Assert.Equal("The trial lasted twelve weeks.", response.Answer);
        Assert.Empty(response.Sources);
    }

    [Fact]
    public void MissingAnswerField_FallsBackToRawText()
    {
        var response = ParseContent("{\"sources\":[]}");
        Assert.Equal("{\"sources\":[]}", response.Answer);
    }

    [Fact]
    public void PageAsString_NoLongerCrashesOrLeaks()
    {
        // Previously page.GetInt32() threw and the WHOLE model JSON was shown.
        var response = ParseContent("{\"answer\":\"A.\",\"sources\":[{\"sourceId\":\"SOURCE_1\",\"page\":\"12\",\"quote\":\"q\"}]}");
        Assert.Equal("A.", response.Answer);
        Assert.Single(response.Sources);
        Assert.Equal("SOURCE_1", response.Sources[0].SourceId);
    }

    [Fact]
    public void SourceIdAsNumber_IsTolerated()
    {
        var response = ParseContent("{\"answer\":\"A.\",\"sources\":[{\"sourceId\":2}]}");
        Assert.Equal("2", response.Sources[0].SourceId);
    }

    [Fact]
    public void EmptyContent_ThrowsBadResponse()
    {
        Assert.Throws<AiProviderException>(() => OpenAiCompatibleProvider.ParseResponse(
            EnvelopeWith("\"\"", "stop")));
    }

    [Fact]
    public void MissingContentField_ThrowsBadResponse()
    {
        Assert.Throws<AiProviderException>(() => OpenAiCompatibleProvider.ParseResponse(
            "{\"choices\":[{\"message\":{\"role\":\"assistant\"},\"finish_reason\":\"stop\"}]}"));
    }

    [Fact]
    public void CutOffWithEmptyContent_ThrowsCutOff()
    {
        var ex = Assert.Throws<AiProviderException>(() => OpenAiCompatibleProvider.ParseResponse(
            EnvelopeWith("\"\"", "length")));
        Assert.Equal(AiErrorCategory.CutOff, ex.Category);
    }

    [Fact]
    public void CutOffWithTruncatedContent_ThrowsCutOff()
    {
        // Truncated JSON as content: previously shown raw. Now a typed CutOff.
        var ex = Assert.Throws<AiProviderException>(() => OpenAiCompatibleProvider.ParseResponse(
            EnvelopeWith("\"{\\\"answer\\\": \\\"half a sen\"", "length")));
        Assert.Equal(AiErrorCategory.CutOff, ex.Category);
    }

    [Fact]
    public void ReasoningFieldPresent_IsIgnored()
    {
        var response = OpenAiCompatibleProvider.ParseResponse(
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"answer\\\":\\\"A.\\\"}\",\"reasoning\":\"chain of thought\"},\"finish_reason\":\"stop\"}]}");
        Assert.Equal("A.", response.Answer);
    }

    [Fact]
    public void MalformedEnvelope_ThrowsBadResponse_NeverRawAnswer()
    {
        var ex = Assert.Throws<AiProviderException>(() =>
            OpenAiCompatibleProvider.ParseResponse("{\"nope\":true}"));
        Assert.Equal(AiErrorCategory.BadResponse, ex.Category);
        // The exception message carries no response body.
        Assert.DoesNotContain("nope", ex.Message);
    }

    [Fact]
    public void UsageLimitBody_IsDistinguishedFromBusy()
    {
        Assert.True(OpenAiCompatibleProvider.IsUsageLimitBody("usage limit reached for your account"));
        Assert.True(OpenAiCompatibleProvider.IsUsageLimitBody("{\"error\":\"quota exceeded\"}"));
        Assert.False(OpenAiCompatibleProvider.IsUsageLimitBody("model is busy, queued"));
        Assert.False(OpenAiCompatibleProvider.IsUsageLimitBody(null));
    }

    [Fact]
    public void ConnectionRefused_ClassifiedAsOllamaNotRunning()
    {
        // Windows wording is "No connection could be made because the target
        // machine actively refused it" - text matching fails, the typed
        // inner SocketException must classify.
        var hre = new HttpRequestException("outer", inner:
            new HttpRequestException("No connection could be made because the target machine actively refused it",
                inner: new SocketException((int)SocketError.ConnectionRefused)));
        Assert.True(OpenAiCompatibleProvider.IsConnectionFailure(hre));
    }

    [Fact]
    public void NonConnectionErrors_AreNotOllamaNotRunning()
    {
        var hre = new HttpRequestException("some request problem");
        Assert.False(OpenAiCompatibleProvider.IsConnectionFailure(hre));
    }
}

public sealed class AiEndpointsTests
{
    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://[::1]:11434/v1", true)]
    [InlineData("http://127.254.9.1:11434", true)]
    [InlineData("http://ollama.example.com", false)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("", false)]
    public void LocalDetection_CoversLoopbackForms(string url, bool expected)
    {
        Assert.Equal(expected, AiEndpoints.IsLocal(url));
    }
}

public sealed class RetrievalQueryTests
{
    [Fact]
    public void ShortStandaloneQuestion_IsNotWidened()
    {
        // 3+ content terms: a complete question, no previous-topic bleed (D5).
        var q = AiChatText.BuildRetrievalQuery("What is the Prophecy of Neferti?", "Who built the pyramids?");
        Assert.Equal("What is the Prophecy of Neferti?", q);
    }

    [Fact]
    public void PronounFollowUp_IsWidened()
    {
        var q = AiChatText.BuildRetrievalQuery("why does he think that?", "Why was the vizier dismissed?");
        Assert.Contains("Why was the vizier dismissed?", q);
        Assert.Contains("why does he think that?", q);
    }

    [Fact]
    public void LongPreviousQuestion_DoesNotCutTheCurrentOne()
    {
        var prev = new string('a', 500);
        var input = "What is the Prophecy of Neferti?";
        var q = AiChatText.BuildRetrievalQuery(input, prev);
        Assert.EndsWith(input, q);                       // the current question survives
        Assert.True(q.Length <= 400);
    }

    [Fact]
    public void StripCitationMarkers_RemovesStaleSourceMarkers()
    {
        var stripped = AiChatText.StripCitationMarkers("The fee doubles. [SOURCE_2] Also 【SOURCE_3】 noted.");
        Assert.DoesNotContain("SOURCE_2", stripped);
        Assert.DoesNotContain("SOURCE_3", stripped);
        Assert.Contains("The fee doubles.", stripped);
    }

    [Fact]
    public void BareBracketNumber_ArrayIndexStaysText()
    {
        // "array[1]" - no left word boundary - must NOT parse as a citation (E11).
        var stripped = AiChatText.StripCitationMarkers("see array[1] for values");
        Assert.Contains("array[1]", stripped);
        Assert.Equal(-1, AiCitations.ParseSourceId("array[1]"));
    }

    [Theory]
    [InlineData("SOURCE_3", 3)]
    [InlineData("source 3", 3)]
    [InlineData("[3]", 3)]
    [InlineData("【SOURCE_3】", 3)]
    [InlineData("3", 3)]
    [InlineData("SOURCE_0", 0)]
    [InlineData("invented", -1)]
    public void SourceIdSpellings_Parse(string id, int expected)
    {
        Assert.Equal(expected, AiCitations.ParseSourceId(id));
    }
}

public sealed class QuoteLocatorTests
{
    private static DocumentChunk Chunk(string text, int[] pages, List<int[]> ranges)
        => new()
        {
            Text = text,
            PageIndices = pages.ToList(),
            WordRanges = ranges
        };

    [Fact]
    public void ExactQuote_OnSecondPage_ResolvesToSecondPage()
    {
        // Page 30 holds chunk words 0..4; page 31 holds words 5..9 (E3).
        var text = "alpha bravo charlie delta echo foxtrot golf hotel india juliet";
        var chunk = Chunk(text, new[] { 29, 30 }, new List<int[]> { new[] { 0, 4 }, new[] { 5, 9 } });

        var hit = QuoteLocator.LocateInChunk(chunk, "Foxtrot Golf Hotel!");

        Assert.Equal(AiQuoteLocation.Exact, hit.State);
        Assert.Equal(30, hit.PageIndex);                     // SECOND page of the chunk
        Assert.Equal(1, hit.PageSlot);
    }

    [Fact]
    public void PunctuationAndCaseRelaxed_Exact()
    {
        var text = "The contractor may terminate for convenience upon thirty days notice.";
        var chunk = Chunk(text, new[] { 5 }, new List<int[]> { new[] { 0, 10 } });
        var hit = QuoteLocator.LocateInChunk(chunk, "TERMINATE for convenience");
        Assert.Equal(AiQuoteLocation.Exact, hit.State);
        Assert.Equal(5, hit.PageIndex);
    }

    [Fact]
    public void RewordedQuote_ApproximateRepair()
    {
        // Model dropped/reworded words: in-order repair still finds the span.
        var text = "the contractor may terminate for convenience upon thirty days written notice";
        var chunk = Chunk(text, new[] { 5 }, new List<int[]> { new[] { 0, 12 } });
        var hit = QuoteLocator.LocateInChunk(chunk, "terminate convenience upon thirty notice");
        Assert.Equal(AiQuoteLocation.Approximate, hit.State);
    }

    [Fact]
    public void InventedQuote_Unlocated()
    {
        var text = "something entirely unrelated happens here";
        var chunk = Chunk(text, new[] { 5 }, new List<int[]> { new[] { 0, 5 } });
        var hit = QuoteLocator.LocateInChunk(chunk, "the moon is made of cheese entirely");
        Assert.Equal(AiQuoteLocation.Unlocated, hit.State);
    }

    [Fact]
    public void EmptyQuote_Unlocated()
    {
        var chunk = Chunk("text", new[] { 0 }, new List<int[]> { new[] { 0, 0 } });
        Assert.Equal(AiQuoteLocation.Unlocated, QuoteLocator.LocateInChunk(chunk, "").State);
        Assert.Equal(AiQuoteLocation.Unlocated, QuoteLocator.LocateInChunk(chunk, null).State);
    }
}

public sealed class EmbeddingStateTests
{
    private readonly string _dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "avalanche_embtests_" + Guid.NewGuid().ToString("N"));

    public EmbeddingStateTests() => System.IO.Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static DocumentIndex MakeDoc(string docId, int chunks) => new()
    {
        DocumentId = docId,
        ContentHash = "HASH" + docId,
        Chunks = Enumerable.Range(0, chunks).Select(i => new DocumentChunk
        {
            ChunkId = $"{docId}_c{i}",
            DocumentId = docId,
            Text = $"chunk text {i}"
        }).ToList()
    };

    [Fact]
    public async Task PrefixChange_InvalidatesAndReembeds()
    {
        using var index = SemanticTestHelpers.NewIndex(_dir, "doc_prefix");
        var doc = MakeDoc("doc_prefix", 2);
        index.PersistDocumentAtomic(doc);

        var calls = 0;
        Task<float[][]> Embed(IReadOnlyList<string> texts, CancellationToken _) { calls++; return Task.FromResult(texts.Select(t => new float[] { 1f, 0f }).ToArray()); }

        var indexer = new DocumentIndexer(index);
        await indexer.EnsureEmbeddingsAsync(doc, Embed, "embeddinggemma:latest", cancellationToken: default,
            documentPrefix: "a: ", queryPrefix: "q: ");
        Assert.Equal(1, calls);

        // Same prefixes: reuse, no embedding calls.
        await indexer.EnsureEmbeddingsAsync(doc, Embed, "embeddinggemma:latest", cancellationToken: default,
            documentPrefix: "a: ", queryPrefix: "q: ");
        Assert.Equal(1, calls);

        // Changed prefix set: full rebuild (D4).
        await indexer.EnsureEmbeddingsAsync(doc, Embed, "embeddinggemma:latest", cancellationToken: default,
            documentPrefix: "b: ", queryPrefix: "q: ");
        Assert.Equal(2, calls);
        var state = index.GetEmbeddingState("doc_prefix")!;
        Assert.Contains("b: ", state.PrefixKey);
    }

    [Fact]
    public async Task BatchRetryOnce_TransientFailureRecovered()
    {
        using var index = SemanticTestHelpers.NewIndex(_dir, "doc_retry");
        var doc = MakeDoc("doc_retry", 1);
        index.PersistDocumentAtomic(doc);

        int calls = 0;
        Task<float[][]> Embed(IReadOnlyList<string> texts, CancellationToken _)
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("transient ollama hiccup");
            return Task.FromResult(texts.Select(t => new float[] { 1f, 0f }).ToArray());
        }

        await new DocumentIndexer(index).EnsureEmbeddingsAsync(doc, Embed, "m");
        Assert.Equal(2, calls);                               // failed once, retried once
        Assert.NotNull(index.GetEmbeddingState("doc_retry"));
    }

    [Fact]
    public async Task Cancellation_StopsThePass()
    {
        using var index = SemanticTestHelpers.NewIndex(_dir, "doc_cancel");
        var doc = MakeDoc("doc_cancel", 40);
        index.PersistDocumentAtomic(doc);

        using var cts = new CancellationTokenSource();
        Task<float[][]> Embed(IReadOnlyList<string> texts, CancellationToken ct)
        {
            cts.Cancel();                                     // cancel mid-pass
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(texts.Select(t => new float[] { 1f, 0f }).ToArray());
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DocumentIndexer(index).EnsureEmbeddingsAsync(doc, Embed, "m", cancellationToken: cts.Token));
        Assert.Null(index.GetEmbeddingState("doc_cancel"));   // never marked complete
        Assert.False(index.IsSemanticChannelReady("doc_cancel", "m", "\u0001\u0001"));
    }

    [Fact]
    public async Task SemanticGate_RequiresCompleteMatchingState()
    {
        using var index = SemanticTestHelpers.NewIndex(_dir, "doc_gate");
        var doc = MakeDoc("doc_gate", 2);
        index.PersistDocumentAtomic(doc);

        Task<float[][]> Embed(IReadOnlyList<string> texts, CancellationToken _) =>
            Task.FromResult(texts.Select(t => new float[] { 1f, 0f }).ToArray());

        var indexer = new DocumentIndexer(index);
        await indexer.EnsureEmbeddingsAsync(doc, Embed, "m", cancellationToken: default,
            documentPrefix: "d: ", queryPrefix: "q: ");

        Assert.True(index.IsSemanticChannelReady("doc_gate", "m", "d: \u0001q: "));
        Assert.False(index.IsSemanticChannelReady("doc_gate", "other-model", "d: \u0001q: "));
        Assert.False(index.IsSemanticChannelReady("doc_gate", "m", "other: \u0001q: "));
    }
}
