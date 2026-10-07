// Features/Summary/ExplainSourceSelection.cs - the Explain handoff's pure half.
//
// The floating action popup's Explain grounds itself in the raw extraction of
// the active page range. Two realities shape that handoff: a request body
// bigger than the model's context is silently cut down by the provider (the
// model then only ever sees the first page and truthfully reports the passage
// missing), and the excerpt is quoted from the AI digest, whose typography
// differs from the extraction's (the non-breaking hyphen in "ice-free", "12
// 700" versus "12,700"). The excerpt is therefore normalized into the source's
// plain characters, and a range too large for one request travels pre-selected:
// whole [p. N] pages scored by word-overlap with the excerpt ride first and the
// far pages stay home. Pure and unit-testable; compiled into the test project
// directly, like MarkdownNormalizer.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    internal static class ExplainSourceSelection
    {
        /// <summary>Character ceiling for the raw source text handed to Explain.
        /// Beyond it SelectExcerptPages ships the pages most related to the
        /// highlighted excerpt instead of a body the provider may cut off
        /// mid-range.</summary>
        internal const int MaxExplainSourceChars = 32000;

        // The page anchor the extraction emits - the compact [p. N] form plus the
        // legacy [[p. N]] form - kept in step with PageSummarizer.PageMarkerPattern.
        private const string PageAnchorPattern = @"\[{1,2}p\.\s*\d+\]{1,2}";

        /// <summary>Folds the typographic variants an AI digest freely uses - the
        /// non-breaking hyphen in "ice-free", curly quotes, narrow and no-break
        /// spaces, minus signs - into the plain forms the extraction uses, and
        /// collapses whitespace runs, so the Explain prompt's excerpt can actually
        /// match the source text's characters.</summary>
        internal static string NormalizeExcerpt(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string folded = text
                .Replace("\u2011", "-").Replace("\u2010", "-").Replace("\u00AD", "-").Replace("\u2212", "-")
                .Replace("\u2018", "'").Replace("\u2019", "'").Replace("\u201B", "'")
                .Replace("\u201C", "\"").Replace("\u201D", "\"").Replace("\u201E", "\"")
                .Replace("\u00A0", " ").Replace("\u202F", " ").Replace("\u2009", " ");
            return System.Text.RegularExpressions.Regex.Replace(folded, "\\s+", " ").Trim();
        }

        // The characters a content word can carry beyond letters and digits: the
        // excerpt's tokens split on these (hyphens included - "ice-free" scores
        // "ice" and "free" against the source, both of which it contains).
        private static readonly char[] ExcerptSplitChars =
        {
            ' ', '-', '\'', '"', ',', '.', ';', ':', '(', ')', '[', ']', '{', '}',
            '\n', '\r', '\t', '!', '?', '/', '\\', '*', '_', '#', '&', '=',
            '\u2014', '\u2013', '\u00AB', '\u00BB', '\u2026'
        };

        /// <summary>The excerpt's content words (4+ characters, lowercased,
        /// punctuation-folded, deduplicated) - the vocabulary page selection
        /// scores the source pages with.</summary>
        private static List<string> ExcerptTokens(string excerpt)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(excerpt))
            {
                return tokens;
            }

            foreach (string raw in NormalizeExcerpt(excerpt).ToLowerInvariant().Split(
                ExcerptSplitChars, StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw.Length >= 4 && !tokens.Contains(raw))
                {
                    tokens.Add(raw);
                }
            }

            return tokens;
        }

        /// <summary>Returns the raw source text within the Explain character budget.
        /// A range that fits goes through untouched - the UNABRIDGED contract. A
        /// larger one is rebuilt from whole [p. N] page blocks: every page is
        /// scored by how many of the excerpt's content words its text contains,
        /// the strongest pages ride first (ties keep page order, the top page is
        /// always taken even if it alone breaks the budget - one page is the floor
        /// of a grounded answer), and the original page order is restored. With no
        /// overlap at all the first pages ride, matching what a blind truncation
        /// would have kept but on clean page boundaries.</summary>
        internal static string SelectExcerptPages(string rawRangeText, string excerpt, int charBudget, out bool trimmed)
        {
            trimmed = false;
            if (string.IsNullOrEmpty(rawRangeText) || rawRangeText.Length <= charBudget)
            {
                return rawRangeText ?? string.Empty;
            }

            var blocks = new List<(int Index, string Text)>();
            int order = 0;
            foreach (string piece in System.Text.RegularExpressions.Regex.Split(
                rawRangeText, "(?=" + PageAnchorPattern + ")"))
            {
                string block = piece.Trim();
                if (block.Length > 0)
                {
                    blocks.Add((order++, block));
                }
            }

            if (blocks.Count == 0)
            {
                trimmed = true;
                return rawRangeText.Length > charBudget ? rawRangeText[..charBudget].TrimEnd() : rawRangeText;
            }

            var tokens = ExcerptTokens(excerpt);
            var scores = new int[blocks.Count];
            for (int b = 0; b < blocks.Count && tokens.Count > 0; b++)
            {
                string page = NormalizeExcerpt(blocks[b].Text).ToLowerInvariant();
                foreach (string token in tokens)
                {
                    if (page.Contains(token))
                    {
                        scores[b]++;
                    }
                }
            }

            var candidates = new List<int>(blocks.Count);
            for (int i = 0; i < blocks.Count; i++)
            {
                candidates.Add(i);
            }

            candidates.Sort((a, b) =>
            {
                int byScore = scores[b].CompareTo(scores[a]);
                return byScore != 0 ? byScore : blocks[a].Index.CompareTo(blocks[b].Index);
            });

            var chosen = new List<int>();
            int used = 0;
            foreach (int b in candidates)
            {
                if (chosen.Count > 0 && used + blocks[b].Text.Length + 2 > charBudget)
                {
                    break;
                }

                chosen.Add(b);
                used += blocks[b].Text.Length + 2;
            }

            chosen.Sort((a, b) => blocks[a].Index.CompareTo(blocks[b].Index));
            trimmed = chosen.Count < blocks.Count;
            var sb = new StringBuilder();
            foreach (int b in chosen)
            {
                if (sb.Length > 0)
                {
                    sb.Append("\n\n");
                }

                sb.Append(blocks[b].Text);
            }

            return sb.ToString();
        }
    }
}
