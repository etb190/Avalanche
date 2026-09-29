using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Hybrid retriever: lexical (BM25/FTS5) + semantic (cosine over
    /// embeddinggemma:latest vectors) with heuristic reranking. The semantic
    /// channel is strictly optional - a missing embedding client, a document
    /// whose vectors are not (yet) stored, or a failed query embedding all
    /// degrade to the proven lexical-only path instead of failing retrieval.
    /// </summary>
    public sealed class HybridRetriever
    {
        private readonly VectorIndex _vectorIndex;
        private readonly OllamaEmbeddingClient? _embeddingClient;
        private readonly RetrievalOptions _options;

        public HybridRetriever(VectorIndex vectorIndex, OllamaEmbeddingClient? embeddingClient = null, RetrievalOptions? options = null)
        {
            _vectorIndex = vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
            _embeddingClient = embeddingClient;
            _options = options ?? new RetrievalOptions();
        }

        /// <summary>
        /// Retrieves relevant chunks using hybrid (lexical + semantic) search
        /// with optional reranking. Falls back to lexical-only when the
        /// embedding layer is unavailable - see the class doc.
        /// </summary>
        public async Task<List<RetrievedChunk>> RetrieveAsync(string documentId, string query, int maxResults = 10,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<RetrievedChunk>();

            // Semantic channel. Two gates keep the fallback FREE of network
            // cost: no embedding client, or no stored vectors for this
            // document yet (embedding pass still running or failed) -> pure
            // lexical without an HTTP round-trip.
            float[] queryEmbedding = Array.Empty<float>();
            bool usedSemantic = false;
            if (_embeddingClient is not null && _vectorIndex.HasEmbeddings(documentId))
            {
                try
                {
                    queryEmbedding = await _embeddingClient.GenerateEmbeddingAsync(query, cancellationToken).ConfigureAwait(false);
                    usedSemantic = queryEmbedding is { Length: > 0 };
                }
                catch (Exception ex)
                {
                    Avalanche.Services.AiHighlightLog.Log(
                        $"retrieval: query embedding failed - lexical-only fallback ({ex.Message})");
                }
            }

            List<RetrievedChunk> results;
            if (usedSemantic)
            {
                results = _vectorIndex.HybridSearch(documentId, query, queryEmbedding, _options.CandidatePoolSize,
                    _options.LexicalWeight, _options.SemanticWeight);
            }
            else
            {
                // Lexical retrieval (BM25 via FTS5). Scores are -bm25, best-first.
                results = _vectorIndex.LexicalSearch(documentId, query, _options.CandidatePoolSize);
                if (results.Count == 0)
                    return new List<RetrievedChunk>();

                VectorIndex.NormalizeScores(results);
            }

            // A single hit has nothing to normalize against; it matched the
            // query (by terms or above the cosine noise floor), so it is
            // relevant by definition. Previously it kept its raw score and the
            // MinScore filter could drop it, reporting "no matches" for an
            // exact-term question.
            if (results.Count == 1) results[0].Score = 1f;

            // Rerank if enabled
            bool reranked = false;
            if (_options.EnableReranking && results.Count > 1)
            {
                results = Rerank(query, results);
                reranked = true;
            }

            // Apply evidence character budget
            var finalResults = ApplyEvidenceBudget(results, _options.EvidenceCharBudget);

            // Apply final scoring and filtering
            finalResults = finalResults
                .Where(r => r.Score >= _options.MinScore)
                .Take(maxResults)
                .ToList();

            // Mark retrieval method
            foreach (var r in finalResults)
            {
                r.Method = reranked ? RetrievalMethod.Reranked
                         : usedSemantic ? RetrievalMethod.Hybrid
                         : RetrievalMethod.Lexical;
            }

            return finalResults;
        }

        /// <summary>
        /// Semantic-only search for conceptual queries (diagnostics and
        /// tests; the normal path is the fused RetrieveAsync).
        /// </summary>
        public async Task<List<RetrievedChunk>> SemanticSearchAsync(string documentId, string query, int maxResults = 10,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query) || _embeddingClient is null)
                return new List<RetrievedChunk>();

            var queryEmbedding = await _embeddingClient.GenerateEmbeddingAsync(query, cancellationToken).ConfigureAwait(false);
            var results = _vectorIndex.SemanticSearch(documentId, queryEmbedding, maxResults);
            foreach (var r in results) r.Method = RetrievalMethod.Semantic;
            return results;
        }

        /// <summary>
        /// Lexical-only search for exact terminology.
        /// </summary>
        public List<RetrievedChunk> LexicalSearch(string documentId, string query, int maxResults = 10)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<RetrievedChunk>();

            var results = _vectorIndex.LexicalSearch(documentId, query, maxResults);
            foreach (var r in results) r.Method = RetrievalMethod.Lexical;
            return results;
        }

        /// <summary>
        /// Applies evidence character budget to keep prompt size bounded.
        /// </summary>
        private List<RetrievedChunk> ApplyEvidenceBudget(List<RetrievedChunk> results, int budget)
        {
            if (budget <= 0) return results;

            var selected = new List<RetrievedChunk>();
            int totalChars = 0;

            foreach (var r in results.OrderByDescending(r => r.Score))
            {
                int chunkChars = r.Chunk.Text.Length;
                if (totalChars + chunkChars > budget && selected.Count > 0)
                    break;

                selected.Add(r);
                totalChars += chunkChars;
            }

            return selected;
        }

        /// <summary>
        /// Reranks candidates using a heuristic approach based on query-term
        /// overlap and position. Pure CPU work - no model call involved.
        /// </summary>
        private List<RetrievedChunk> Rerank(string query, List<RetrievedChunk> candidates)
        {
            if (candidates.Count <= 1) return candidates;

            var reranked = new List<RetrievedChunk>();

            var queryTerms = query.ToLowerInvariant()
                .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2)
                .Distinct()
                .ToList();

            foreach (var candidate in candidates)
            {
                float rerankScore = candidate.Score;

                // Boost for query term density
                var textLower = candidate.Chunk.Text.ToLowerInvariant();
                int termMatches = 0;
                foreach (var term in queryTerms)
                {
                    if (textLower.Contains(term))
                        termMatches++;
                }

                float termDensity = queryTerms.Count > 0 ? (float)termMatches / queryTerms.Count : 0f;
                rerankScore = (rerankScore * 0.7f) + (termDensity * 0.3f);

                // Boost for chunks with headings matching query
                if (!string.IsNullOrEmpty(candidate.Chunk.SectionHeading))
                {
                    var headingLower = candidate.Chunk.SectionHeading.ToLowerInvariant();
                    foreach (var term in queryTerms)
                    {
                        if (headingLower.Contains(term))
                        {
                            rerankScore += 0.1f;
                            break;
                        }
                    }
                }

                // Slight boost for earlier chunks (intro often has overview)
                if (candidate.Chunk.ChunkIndex < 5)
                    rerankScore += 0.02f;

                reranked.Add(new RetrievedChunk
                {
                    Chunk = candidate.Chunk,
                    Score = Math.Min(rerankScore, 1.0f),
                    Method = RetrievalMethod.Reranked
                });
            }

            return reranked
                .OrderByDescending(r => r.Score)
                .ToList();
        }
    }

    /// <summary>
    /// Configuration for retrieval behavior.
    /// </summary>
    public sealed class RetrievalOptions
    {
        public int CandidatePoolSize { get; set; } = 30;      // Initial pool before reranking
        public int MaxResults { get; set; } = 10;             // Final results
        public int TopK { get; set; } = 8;                    // Number of chunks to retrieve
        public int EvidenceCharBudget { get; set; } = 12000;  // Total character budget for evidence
        public float MinScore { get; set; } = 0.15f;          // Minimum relevance score
        public bool EnableReranking { get; set; } = true;     // Apply reranking
        public int MaxRerankCandidates { get; set; } = 20;    // Max candidates to rerank
        public float LexicalWeight { get; set; } = 0.4f;      // BM25 channel weight in the fusion
        public float SemanticWeight { get; set; } = 0.6f;     // Cosine channel weight in the fusion
    }
}
