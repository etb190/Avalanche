using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Checks that a model-supplied quote actually occurs in the cited chunk's
    /// text. LLMs paraphrase or invent quotes; an unverified quote must never
    /// drive the PDF highlight (it would paint an unrelated passage), so the
    /// chat view model records the verdict and the highlighter drops quotes
    /// that failed. The comparison is relaxed - lowercase, letters and digits
    /// only - so line breaks, whitespace, punctuation, curly quotes and soft
    /// hyphens cannot cause false negatives.
    /// </summary>
    public static class PassageQuoteValidator
    {
        /// <summary>Normalized letters+digits of the quote must appear in the
        /// chunk (or >= 80% of its words must appear in order). Quotes shorter
        /// than 8 normalized characters return false: too little text to
        /// verify, so they are treated as unverified rather than trusted.</summary>
        public static bool IsValidQuote(string? quote, string? chunkText)
        {
            var quoteWords = NormalizeWords(quote).ToList();
            if (quoteWords.Count == 0) return false;

            var joined = string.Concat(quoteWords);
            if (joined.Length < 8) return false;

            var chunkWords = NormalizeWords(chunkText).ToList();
            if (chunkWords.Count == 0) return false;

            if (string.Concat(chunkWords).Contains(joined, StringComparison.Ordinal))
                return true;

            // In-order subsequence fallback: tolerate a few dropped or reworded
            // words, but require most of the quote to appear in order.
            int matched = 0, ci = 0;
            foreach (var w in quoteWords)
            {
                while (ci < chunkWords.Count && chunkWords[ci] != w) ci++;
                if (ci < chunkWords.Count) { matched++; ci++; }
            }
            int needed = Math.Max(3, (int)Math.Ceiling(quoteWords.Count * 0.8));
            return matched >= needed;
        }

        private static IEnumerable<string> NormalizeWords(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) yield break;
            foreach (var raw in s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var sb = new StringBuilder(raw.Length);
                foreach (var ch in raw)
                {
                    if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                }
                if (sb.Length > 0) yield return sb.ToString();
            }
        }
    }
}
