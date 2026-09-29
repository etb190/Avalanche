using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalanche.Features.AI;
using Xunit;

namespace Avalanche.Tests;

public sealed class DocumentIndexerEmbeddingTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "avalanche_idxtests_" + Guid.NewGuid().ToString("N"));

    public DocumentIndexerEmbeddingTests() => System.IO.Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class CountingEmbedder
    {
        public int Calls;
        public List<List<string>> Batches { get; } = new();

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++;
            Batches.Add(texts.ToList());
            return Task.FromResult(texts.Select((t, i) =>
                new[] { (float)texts.Count, (float)i, 0f, 1f }).ToArray());
        }
    }

    private static DocumentIndex MakeDoc()
        => SemanticTestHelpers.MakeIndex("doc_emb",
            (30, SemanticTestHelpers.TerminationText),
            (5, SemanticTestHelpers.LocationText),
            (12, SemanticTestHelpers.PaymentText));

    [Fact]
    public async Task EnsureEmbeddings_EmbedsAllChunks_AndRecordsState()
    {
        using var index = SemanticTestHelpers.NewIndex(_dir, "all");
        var doc = MakeDoc();
        index.PersistDocumentAtomic(doc);
        var embedder = new CountingEmbedder();

        await new DocumentIndexer(index).EnsureEmbeddingsAsync(
            doc, embedder.EmbedAsync, "embeddinggemma:latest");

        Assert.Equal(1, embedder.Calls);                       // one batch of 3
        Assert.Equal(3, embedder.Batches[0].Count);
        Assert.Equal(3, index.GetEmbeddedChunkIds("doc_emb").Count);

        var state = index.GetEmbeddingState("doc_emb");
        Assert.NotNull(state);
        Assert.Equal("embeddinggemma:latest", state!.Model);
        Assert.Equal("HASH1", state.ContentHash);
        Assert.Equal(3, state.ChunkCount);
        Assert.Equal(4, state.Dim);
    }

    [Fact]
    public async Task ReopeningUnchangedDocument_DoesNotReembed()
    {
        using var index = SemanticTestHelpers.NewIndex(_dir, "reuse");
        var doc = MakeDoc();
        index.PersistDocumentAtomic(doc);
        var embedder = new CountingEmbedder();
        var indexer = new DocumentIndexer(index);

        await indexer.EnsureEmbeddingsAsync(doc, embedder.EmbedAsync, "embeddinggemma:latest");
        int afterFirst = embedder.Calls;

        // Second open of the SAME file (same content hash, same chunk count):
        // the fast path must short-circuit before any embedder call.
        await indexer.EnsureEmbeddingsAsync(doc, embedder.EmbedAsync, "embeddinggemma:latest");

        Assert.Equal(afterFirst, embedder.Calls);
    }

    [Fact]
    public async Task InterruptedPass_ResumesOnlyMissingChunks()
    {
        using var index = SemanticTestHelpers.NewIndex(_dir, "resume");
        var doc = MakeDoc();
        index.PersistDocumentAtomic(doc);

        // Simulate a crash after chunk 0 was embedded (no state row).
        index.InsertEmbeddings("doc_emb", new List<(string, float[])>
        {
            (doc.Chunks[0].ChunkId, new[] { 1f, 0f, 0f, 0f }),
        });

        var embedder = new CountingEmbedder();
        await new DocumentIndexer(index).EnsureEmbeddingsAsync(
            doc, embedder.EmbedAsync, "embeddinggemma:latest");

        Assert.Equal(1, embedder.Calls);
        Assert.Equal(2, embedder.Batches[0].Count);            // only the missing two
        Assert.Equal(3, index.GetEmbeddedChunkIds("doc_emb").Count);
        Assert.NotNull(index.GetEmbeddingState("doc_emb"));
    }

    [Fact]
    public async Task ChangedFileHash_TriggersFreshEmbedding()
    {
        using var index = SemanticTestHelpers.NewIndex(_dir, "hash");
        var doc = MakeDoc();
        index.PersistDocumentAtomic(doc);
        var embedder = new CountingEmbedder();
        var indexer = new DocumentIndexer(index);
        await indexer.EnsureEmbeddingsAsync(doc, embedder.EmbedAsync, "embeddinggemma:latest");

        // File edited: re-indexed under the SAME document id with a new
        // content hash. Stale vectors (here: an orphaned id from a previous
        // build whose chunks no longer exist) must be dropped.
        index.InsertEmbeddings("doc_emb", new List<(string, float[])>
        {
            ("chunk_stale_orphan", new[] { 9f, 9f, 9f, 9f }),
        });

        var docV2 = SemanticTestHelpers.MakeIndex("doc_emb",
            (30, "The agreement may be terminated after forty five days notice."),
            (5, SemanticTestHelpers.LocationText),
            (12, SemanticTestHelpers.PaymentText),
            (31, "A new page added by the edit."));
        docV2.ContentHash = "HASH2";
        index.PersistDocumentAtomic(docV2);

        await indexer.EnsureEmbeddingsAsync(docV2, embedder.EmbedAsync, "embeddinggemma:latest");

        var ids = index.GetEmbeddedChunkIds("doc_emb");
        Assert.Equal(4, ids.Count);
        Assert.DoesNotContain("chunk_stale_orphan", ids);    // stale vector dropped
        Assert.Contains(docV2.Chunks[3].ChunkId, ids);        // new chunk covered
        Assert.Equal("HASH2", index.GetEmbeddingState("doc_emb")!.ContentHash);
    }

    [Fact]
    public async Task ChangedEmbeddingModel_TriggersFreshEmbedding()
    {
        using var index = SemanticTestHelpers.NewIndex(_dir, "model");
        var doc = MakeDoc();
        index.PersistDocumentAtomic(doc);
        var embedder = new CountingEmbedder();
        var indexer = new DocumentIndexer(index);

        await indexer.EnsureEmbeddingsAsync(doc, embedder.EmbedAsync, "some-old-model");
        int afterOld = embedder.Calls;

        await indexer.EnsureEmbeddingsAsync(doc, embedder.EmbedAsync, "embeddinggemma:latest");

        Assert.True(embedder.Calls > afterOld);                // re-embedded
        Assert.Equal("embeddinggemma:latest", index.GetEmbeddingState("doc_emb")!.Model);
    }

    [Fact]
    public async Task MalformedBatch_Throws_AndCallerDecidesFallback()
    {
        using var index = SemanticTestHelpers.NewIndex(_dir, "bad");
        var doc = MakeDoc();
        index.PersistDocumentAtomic(doc);

        static Task<float[][]> Bad(IReadOnlyList<string> texts, CancellationToken ct)
            => Task.FromResult(new float[][] { new[] { 1f, 0f } }); // 2 vectors missing

        var indexer = new DocumentIndexer(index);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            indexer.EnsureEmbeddingsAsync(doc, Bad, "embeddinggemma:latest"));

        // The failed batch stored nothing; a later pass can recover.
        Assert.Empty(index.GetEmbeddedChunkIds("doc_emb"));
    }
}
