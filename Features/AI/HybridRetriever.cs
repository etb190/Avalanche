using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Hybrid retriever combining lexical and semantic search with reranking.
    /// </summary>
    public sealed class HybridRetriever
    {
        private readonly VectorIndex _vectorIndex;
        private readonly IEmbeddingProvider _embeddingProvider;
        private readonly RetrievalOptions _options;

        public HybridRetriever(VectorIndex vectorIndex, IEmbeddingProvider embeddingProvider, RetrievalOptions? options = null)
        {
            _vectorIndex = vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
            _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
            _options = options ?? new RetrievalOptions();
        }

        /// <summary>
        /// Retrieves relevant chunks using hybrid search with optional reranking.
        /// </summary>
        public async Task<List<RetrievedChunk>> RetrieveAsync(string documentId, string query, int maxResults = 10)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<RetrievedChunk>();

            // Generate query embedding with query prefix
            var queryEmbedding = await _embeddingProvider.GenerateEmbeddingAsync(query);

            // Hybrid search: lexical + semantic
            var hybridResults = _vectorIndex.HybridSearch(documentId, query, queryEmbedding, _options.CandidatePoolSize, 
                _options.LexicalWeight, _options.SemanticWeight);

            // Rerank if enabled
            if (_options.EnableReranking && hybridResults.Count > 1)
            {
                hybridResults = await RerankAsync(query, hybridResults);
            }

            // Apply evidence character budget
            var finalResults = ApplyEvidenceBudget(hybridResults, _options.EvidenceCharBudget);

            // Apply final scoring and filtering
            finalResults = finalResults
                .Where(r => r.Score >= _options.MinScore)
                .Take(maxResults)
                .ToList();

            // Mark retrieval method
            foreach (var r in finalResults)
            {
                r.Method = RetrievalMethod.Hybrid;
            }

            return finalResults;
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
        /// Semantic-only search for conceptual queries.
        /// </summary>
        public async Task<List<RetrievedChunk>> SemanticSearchAsync(string documentId, string query, int maxResults = 10)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<RetrievedChunk>();

            var queryEmbedding = await _embeddingProvider.GenerateEmbeddingAsync(query);
            var results = _vectorIndex.SemanticSearch(documentId, queryEmbedding, maxResults);
            foreach (var r in results) r.Method = RetrievalMethod.Semantic;
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
        /// Reranks candidates using a cross-encoder or heuristic approach.
        /// </summary>
        private async Task<List<RetrievedChunk>> RerankAsync(string query, List<RetrievedChunk> candidates)
        {
            if (candidates.Count <= 1) return candidates;

            // Heuristic reranking based on query-term overlap and position
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
        public float LexicalWeight { get; set; } = 0.4f;      // Weight for BM25 scores
        public float SemanticWeight { get; set; } = 0.6f;     // Weight for embedding scores
        public bool EnableReranking { get; set; } = true;     // Apply reranking
        public int MaxRerankCandidates { get; set; } = 20;    // Max candidates to rerank
    }
}