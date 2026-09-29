using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalanche.Features.AI;
using Xunit;

namespace Avalanche.Tests;

public sealed class HybridRetrieverSemanticTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "avalanche_retrtests_" + Guid.NewGuid().ToString("N"));

    public HybridRetrieverSemanticTests() => System.IO.Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Question = "What are the requirements for terminating the agreement?";

    private async Task<VectorIndex> SeedAsync(string docId)
    {
        var index = SemanticTestHelpers.NewIndex(_dir, docId);
        var doc = SemanticTestHelpers.MakeIndex(docId,
            (30, SemanticTestHelpers.TerminationText),
            (5, SemanticTestHelpers.LocationText),
            (12, SemanticTestHelpers.PaymentText));
        index.PersistDocumentAtomic(doc);

        // Realistic-ish vectors: the query direction ("terminate") is closest
        // to the termination chunk; the unrelated chunks sit far off-axis.
        var handler = new FakeEmbedHandler();
        handler.VectorMap[SemanticTestHelpers.TerminationText] = new[] { 0.95f, 0.05f, 0f, 0f };
        handler.VectorMap[SemanticTestHelpers.LocationText] = new[] { 0.02f, 0.99f, 0f, 0f };
        handler.VectorMap[SemanticTestHelpers.PaymentText] = new[] { 0.03f, 0.02f, 0.99f, 0f };
        handler.VectorMap[Question] = new[] { 1f, 0f, 0f, 0f };
        handler.VectorMap["capability probe"] = new[] { 0f, 0f, 0f, 1f };

        var client = new OllamaEmbeddingClient(() => new AiProviderConfig(), 32, handler);
        await new DocumentIndexer(index).EnsureEmbeddingsAsync(
            doc, (texts, ct) => client.GenerateEmbeddingsAsync(texts, ct), "embeddinggemma:latest");
        return index;
    }

    [Fact]
    public async Task TerminationQuestion_RanksTerminationChunkFirst_WithPageNumber()
    {
        using var index = await SeedAsync("doc_rank");
        var handler = new FakeEmbedHandler();
        handler.VectorMap[Question] = new[] { 1f, 0f, 0f, 0f };
        var retriever = new HybridRetriever(index,
            new OllamaEmbeddingClient(() => new AiProviderConfig(), 32, handler),
            new RetrievalOptions { EnableReranking = false, MinScore = 0.15f });

        var results = await retriever.RetrieveAsync("doc_rank", Question, 8);

        Assert.NotEmpty(results);
        Assert.Equal(SemanticTestHelpers.TerminationText, results[0].Chunk.Text);
        Assert.Equal(30, results[0].Chunk.PageNumber);
        Assert.True(results[0].Score >= results[^1].Score);
    }

    [Fact]
    public async Task NullClient_FallsBackToPureLexical_NoHttpPossible()
    {
        using var index = await SeedAsync("doc_lex");
        var retriever = new HybridRetriever(index, embeddingClient: null,
            new RetrievalOptions { EnableReranking = false });

        var results = await retriever.RetrieveAsync("doc_lex", "terminated notice", 8);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(RetrievalMethod.Lexical, r.Method));
    }

    [Fact]
    public async Task DocumentWithoutVectors_Yet_AnswersLexically_WithoutEmbedderCall()
    {
        // Lexical index exists, embedding pass has NOT run (or failed):
        // retrieval must work instantly and never reach the embedder.
        var handler = new FakeEmbedHandler();
        using var index = SemanticTestHelpers.NewIndex(_dir, "novectors");
        var doc = SemanticTestHelpers.MakeIndex("novectors",
            (30, SemanticTestHelpers.TerminationText));
        index.PersistDocumentAtomic(doc);

        var retriever = new HybridRetriever(index,
            new OllamaEmbeddingClient(() => new AiProviderConfig(), 32, handler),
            new RetrievalOptions { EnableReranking = false });

        var results = await retriever.RetrieveAsync("novectors", "terminated", 8);

        Assert.NotEmpty(results);
        Assert.Equal(0, handler.RequestCount);                  // zero network
        Assert.All(results, r => Assert.Equal(RetrievalMethod.Lexical, r.Method));
    }

    [Fact]
    public async Task QueryEmbeddingFailure_DegradesToLexical()
    {
        using var index = await SeedAsync("doc_fail");
        var handler = new FakeEmbedHandler
        {
            Responder = _ => new System.Net.Http.HttpResponseMessage(
                System.Net.HttpStatusCode.InternalServerError)
        };
        var retriever = new HybridRetriever(index,
            new OllamaEmbeddingClient(() => new AiProviderConfig(), 32, handler),
            new RetrievalOptions { EnableReranking = false });

        var results = await retriever.RetrieveAsync("doc_fail", "terminated notice", 8);

        Assert.NotEmpty(results);                               // lexical still answers
        Assert.All(results, r => Assert.Equal(RetrievalMethod.Lexical, r.Method));
    }

    [Fact]
    public async Task RetrievalOptions_CarryFusionWeights()
    {
        var options = new RetrievalOptions();
        Assert.Equal(0.4f, options.LexicalWeight, 3);
        Assert.Equal(0.6f, options.SemanticWeight, 3);
        await Task.CompletedTask;
    }
}
