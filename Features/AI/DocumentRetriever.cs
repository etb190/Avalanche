using System.Collections.Generic;
using System.Linq;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Retrieves relevant chunks from a document index based on a query.
    /// </summary>
    internal sealed class DocumentRetriever
    {
        /// <summary>
        /// Retrieves the most relevant chunks for a query.
        /// </summary>
        public static List<RetrievedChunk> Retrieve(DocumentIndex index, string query, int maxResults = 5)
        {
            if (index == null || index.Chunks.Count == 0 || string.IsNullOrWhiteSpace(query))
                return new List<RetrievedChunk>();

            var queryTerms = query.ToLowerInvariant().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (queryTerms.Length == 0) return new List<RetrievedChunk>();

            var scoredChunks = new List<RetrievedChunk>();

            foreach (var chunk in index.Chunks)
            {
                float score = (float)CalculateScore(chunk.Text, queryTerms);
                if (score > 0)
                {
                    scoredChunks.Add(new RetrievedChunk { Chunk = chunk, Score = score });
                }
            }

            // Sort by score descending and take top results
            return scoredChunks
                .OrderByDescending(r => r.Score)
                .Take(maxResults)
                .ToList();
        }

        /// <summary>
        /// Calculates a relevance score for a chunk against query terms.
        /// </summary>
        private static double CalculateScore(string text, string[] queryTerms)
        {
            var textLower = text.ToLowerInvariant();
            double score = 0;

            foreach (var term in queryTerms)
            {
                if (string.IsNullOrEmpty(term)) continue;

                // Exact phrase match gets highest score
                if (textLower.Contains(term))
                {
                    int count = CountOccurrences(textLower, term);
                    score += count * 10.0;
                }

                // Word boundary matches
                var words = textLower.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in words)
                {
                    if (word == term)
                        score += 5.0;
                    else if (word.StartsWith(term) || word.EndsWith(term))
                        score += 2.0;
                    else if (word.Contains(term))
                        score += 1.0;
                }
            }

            // Normalize by text length (shorter chunks with matches rank higher)
            if (text.Length > 0)
                score = score / (1 + Math.Log(text.Length));

            return score;
        }

        private static int CountOccurrences(string text, string term)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(term, index, System.StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += term.Length;
            }
            return count;
        }
    }
}