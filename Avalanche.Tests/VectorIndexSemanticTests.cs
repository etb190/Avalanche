using System;
using System.Collections.Generic;
using System.Linq;
using Avalanche.Features.AI;
using Xunit;

namespace Avalanche.Tests;

public sealed class VectorIndexSemanticTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "avalanche_vectests_" + Guid.NewGuid().ToString("N"));

    public VectorIndexSemanticTests() => System.IO.Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ------------------------------------------------------- cosine math

    [Fact]
    public void Cosine_IdenticalVectors_IsOne()
    {
        float[] a = { 0.3f, -1.2f, 4f, 0f };
        Assert.Equal(1f, VectorIndex.CosineSimilarity(a, a), 5);
    }

    [Fact]
    public void Cosine_OrthogonalVectors_IsZero()
    {
        Assert.Equal(0f, VectorIndex.CosineSimilarity(new[] { 1f, 0f }, new[] { 0f, 1f }), 5);
    }

    [Fact]
    public void Cosine_OppositeVectors_IsMinusOne()
    {
        Assert.Equal(-1f, VectorIndex.CosineSimilarity(new[] { 2f, 0f }, new[] { -3f, 0f }), 5);
    }

    [Fact]
    public void Cosine_DimensionMismatch_IsZero_NotCrash()
    {
        Assert.Equal(0f, VectorIndex.CosineSimilarity(new[] { 1f, 0f, 0f }, new[] { 1f, 0f }));
    }

    [Fact]
    public void Cosine_ZeroVector_IsZero_NotNaN()
    {
        Assert.Equal(0f, VectorIndex.CosineSimilarity(new[] { 0f, 0f }, new[] { 1f, 0f }));
    }

    // ------------------------------------------- persistence + retrieval

    [Fact]
    public void Embeddings_PersistAndRankAfterReopen_WithoutRegeneration()
    {
        string dbPath = System.IO.Path.Combine(_dir, $"persist_{Guid.NewGuid():N}.db");
        var doc = SemanticTestHelpers.MakeIndex("doc_persist",
            (30, SemanticTestHelpers.TerminationText),
            (5, SemanticTestHelpers.LocationText),
            (12, SemanticTestHelpers.PaymentText));

        using (var index = new VectorIndex(dbPath))
        {
            index.PersistDocumentAtomic(doc);
            var vectors = doc.Chunks.ToDictionary(c => c.ChunkId, c => new float[] { 1f, 0f, 0f, 0f });
            index.InsertEmbeddings(doc.DocumentId,
                doc.Chunks.Select(c => (c.ChunkId, vectors[c.ChunkId])).ToList());
            index.SetEmbeddingState(doc.DocumentId, "embeddinggemma:latest", doc.ContentHash, 4, doc.Chunks.Count);
        }

        // Reopen: a FRESH instance on the same file must retrieve the stored
        // vectors with no embedding regeneration (that is the whole point of
        // the state row + vectors tables).
        using (var reopened = new VectorIndex(dbPath))
        {
            var state = reopened.GetEmbeddingState("doc_persist");
            Assert.NotNull(state);
            Assert.Equal("embeddinggemma:latest", state!.Model);
            Assert.Equal("HASH1", state.ContentHash);
            Assert.Equal(4, state.Dim);
            Assert.Equal(3, state.ChunkCount);

            var results = reopened.SemanticSearch("doc_persist", new[] { 1f, 0f, 0f, 0f }, 10);
            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal(1f, r.Score, 4)); // identical vectors
        }
    }

    [Fact]
    public void SemanticSearch_RanksByCosine_AndPreservesPageMetadata()
    {
        var doc = SemanticTestHelpers.MakeIndex("doc_rank",
            (30, SemanticTestHelpers.TerminationText),
            (5, SemanticTestHelpers.LocationText),
            (12, SemanticTestHelpers.PaymentText));

        using var index = SemanticTestHelpers.NewIndex(_dir, "rank");
        index.PersistDocumentAtomic(doc);
        index.InsertEmbeddings("doc_rank", new List<(string, float[])>
        {
            (doc.Chunks[0].ChunkId, new[] { 0.9f, 0.1f, 0f, 0f }),   // termination - closest
            (doc.Chunks[1].ChunkId, new[] { 0.15f, 0.9f, 0.1f, 0f }), // New York - far
            (doc.Chunks[2].ChunkId, new[] { 0.1f, 0.1f, 0.95f, 0f }), // payment - middle
        });

        var query = new[] { 1f, 0f, 0f, 0f }; // "termination" direction
        var results = index.SemanticSearch("doc_rank", query, 10);

        Assert.True(results.Count >= 2);
        Assert.Equal(doc.Chunks[0].ChunkId, results[0].Chunk.ChunkId);
        Assert.Equal(30, results[0].Chunk.PageNumber); // page metadata preserved
        Assert.Equal(RetrievalMethod.Semantic, results[0].Method);
        Assert.True(results[0].Score > results[^1].Score);
    }

    [Fact]
    public void SemanticSearch_DimensionMismatch_ReturnsEmpty_NotCrash()
    {
        var doc = SemanticTestHelpers.MakeIndex("doc_dim", (1, "hello world"));
        using var index = SemanticTestHelpers.NewIndex(_dir, "dim");
        index.PersistDocumentAtomic(doc);
        index.InsertEmbeddings("doc_dim", new List<(string, float[])>
        {
            (doc.Chunks[0].ChunkId, new[] { 1f, 0f, 0f, 0f }),
        });

        var results = index.SemanticSearch("doc_dim", new[] { 1f, 0f }, 10); // wrong dim
        Assert.Empty(results);
    }

    [Fact]
    public void DeleteEmbeddingsForDocument_RemovesAllVectors()
    {
        var doc = SemanticTestHelpers.MakeIndex("doc_del", (1, "a b c"), (2, "d e f"));
        using var index = SemanticTestHelpers.NewIndex(_dir, "del");
        index.PersistDocumentAtomic(doc);
        index.InsertEmbeddings("doc_del", new List<(string, float[])>
        {
            (doc.Chunks[0].ChunkId, new[] { 1f, 0f }),
            (doc.Chunks[1].ChunkId, new[] { 0f, 1f }),
        });
        Assert.True(index.HasEmbeddings("doc_del"));

        index.DeleteEmbeddingsForDocument("doc_del");
        Assert.False(index.HasEmbeddings("doc_del"));
        Assert.Empty(index.GetEmbeddedChunkIds("doc_del"));
    }

    [Fact]
    public void HybridSearch_FusesBothChannels_WeightedTowardSemantic()
    {
        // Chunk A matches LEXICALLY (shares "terminated"), chunk B matches
        // SEMANTICALLY only. With 0.6 semantic weight the semantic-only hit
        // must outrank the lexical-only hit when its cosine is high.
        var doc = SemanticTestHelpers.MakeIndex("doc_hybrid",
            (7, "The contract may be terminated by either party."),
            (9, "The office tower is located in Manhattan."));

        using var index = SemanticTestHelpers.NewIndex(_dir, "hybrid");
        index.PersistDocumentAtomic(doc);
        index.InsertEmbeddings("doc_hybrid", new List<(string, float[])>
        {
            (doc.Chunks[0].ChunkId, new[] { 0.2f, 0.1f, 0f, 0f }),  // weak semantic
            (doc.Chunks[1].ChunkId, new[] { 0.95f, 0.2f, 0f, 0f }), // strong semantic
        });

        var queryVector = new[] { 1f, 0.2f, 0f, 0f };
        var results = index.HybridSearch("doc_hybrid", "terminated contract", queryVector, 10, 0.4f, 0.6f);

        Assert.Equal(2, results.Count);
        // Chunk 1 (semantic-only, 0.6 * ~1) beats chunk 0 (lexical-only, 0.4 * 1).
        Assert.Equal(doc.Chunks[1].ChunkId, results[0].Chunk.ChunkId);
        Assert.Equal(9, results[0].Chunk.PageNumber);
        Assert.Equal(RetrievalMethod.Hybrid, results[0].Method);
    }
}
