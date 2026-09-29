using System;
using System.Collections.Generic;
using System.Linq;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Locates the model's quote INSIDE the cited chunk's text (never elsewhere
    /// in the PDF) and maps the match to the chunk page that carries it plus a
    /// page-relative word range. This drives which page a citation navigates
    /// to (a quote on a chunk's second page must go to the SECOND page) and
    /// whether it may highlight at all.
    /// No WPF, no I/O - unit-testable.
    /// </summary>
    public static class QuoteLocator
    {
        public sealed class QuoteHit
        {
            public AiQuoteLocation State { get; init; }
            /// <summary>0-based page index (into the DOCUMENT) the quote sits on.</summary>
            public int PageIndex { get; init; }
            /// <summary>[start, end] CHUNK-relative word range of the quote.</summary>
            public int[] ChunkWordRange { get; init; } = Array.Empty<int>();
            /// <summary>Index of the page within the chunk (0-based into PageIndices).</summary>
            public int PageSlot { get; init; }
        }

        /// <summary>
        /// Exact: the quote's normalized words appear consecutively in the
        /// chunk. Approximate: an in-order repair covering at least 80% of the
        /// quote's words (the same bar PassageQuoteValidator uses). Unlocated
        /// otherwise. Short quotes (under 3 words) are never approximated -
        /// too little text to place trust in.
        /// </summary>
        public static QuoteHit LocateInChunk(DocumentChunk chunk, string? quote)
        {
            if (chunk is null || string.IsNullOrWhiteSpace(quote) || chunk.PageIndices.Count == 0)
                return Unlocated(chunk);

            var chunkWords = SplitNormalize(chunk.Text);
            if (chunkWords.Length == 0)
                return Unlocated(chunk);

            var needle = SplitNormalize(quote);
            if (needle.Length == 0)
                return Unlocated(chunk);

            // Pass A: exact consecutive sequence.
            var hit = SequencePass(chunkWords, needle);
            var state = AiQuoteLocation.Exact;

            // Pass B: in-order repair - first..last matched word bounds a window
            // holding at least 80% of the needle (>=3 words).
            if (hit is null)
            {
                hit = RepairPass(chunkWords, needle);
                state = AiQuoteLocation.Approximate;
            }

            if (hit is null)
                return Unlocated(chunk);

            var (start, end) = hit.Value;

            // Map the chunk-relative range onto the page that carries most of it.
            foreach (var slot in Enumerable.Range(0, chunk.PageIndices.Count))
            {
                if (slot >= chunk.WordRanges.Count) break;
                var range = chunk.WordRanges[slot];
                if (range is null || range.Length < 2) continue;

                int s = range[0], e = range[1];
                if (start >= s && end <= e)
                {
                    return new QuoteHit
                    {
                        State = state,
                        PageIndex = chunk.PageIndices[slot],
                        ChunkWordRange = new[] { start, end },
                        PageSlot = slot
                    };
                }
            }

            // Range crosses page boundaries: fall back to the page holding the
            // quote's first word.
            for (int slot = 0; slot < chunk.PageIndices.Count && slot < chunk.WordRanges.Count; slot++)
            {
                var range = chunk.WordRanges[slot];
                if (range is null || range.Length < 2) continue;
                if (start >= range[0] && start <= range[1])
                {
                    return new QuoteHit
                    {
                        State = state,
                        PageIndex = chunk.PageIndices[slot],
                        ChunkWordRange = new[] { start, Math.Min(end, range[1]) },
                        PageSlot = slot
                    };
                }
            }

            return Unlocated(chunk);
        }

        private static QuoteHit Unlocated(DocumentChunk chunk) => new()
        {
            State = AiQuoteLocation.Unlocated,
            PageIndex = chunk?.PageIndices.Count > 0 ? chunk.PageIndices[0] : -1,
            ChunkWordRange = Array.Empty<int>(),
            PageSlot = 0
        };

        private static string[] SplitNormalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeWord)
                .Where(w => w.Length > 0)
                .ToArray();
        }

        private static string NormalizeWord(string word)
        {
            var sb = new System.Text.StringBuilder(word.Length);
            foreach (var ch in word)
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        private static (int Start, int End)? SequencePass(string[] items, string[] needle)
        {
            int n = needle.Length;
            for (int i = 0; i + n <= items.Length; i++)
            {
                bool ok = true;
                for (int k = 0; k < n; k++)
                {
                    if (items[i + k] != needle[k]) { ok = false; break; }
                }
                if (ok) return (i, i + n - 1);
            }
            return null;
        }

        private static (int Start, int End)? RepairPass(string[] items, string[] needle)
        {
            if (needle.Length < 3) return null;

            // Two-pointer in-order subsequence: first and last matched words
            // bound the window; the quote is trusted only when >=80% of its
            // words were found in order (PassageQuoteValidator's bar).
            int matched = 0, first = -1, last = -1, ci = 0;
            foreach (var w in needle)
            {
                while (ci < items.Length && items[ci] != w) ci++;
                if (ci < items.Length)
                {
                    if (first < 0) first = ci;
                    last = ci;
                    matched++;
                    ci++;
                }
            }

            int needed = Math.Max(3, (int)Math.Ceiling(needle.Length * 0.8));
            if (first < 0 || matched < needed)
                return null;

            return (first, last);
        }
    }
}
