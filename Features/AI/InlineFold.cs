// Features/AI/InlineFold.cs - the soft-break fold for split inline markers.
//
// The models hard-wrap their markdown. A **bold** whose wrap point fell
// inside the markers reached the inline parser as an opener on one line and
// a closer on the next: the digest's justified paragraphs joined their
// soft-broken lines before parsing (AiMarkdown's pending group), but a
// bullet carried its opener alone, the left-aligned chat bubbles keep one
// paragraph per line by design, and both asterisk pairs rendered as literal
// asterisks. This fold reunites what the pen split BEFORE the parser sees
// it: a line whose ** markers count odd pulls the plain line beneath into
// its own text until the count balances - never across a blank line, a
// fence, or a block of its own (heading, rule, list, quote), because
// emphasis never crosses a paragraph boundary. No WPF, no I/O - the class
// is unit-tested against synthetic lines (InlineFoldTests).

namespace Avalanche.Features.AI
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    internal static class InlineFold
    {
        private static readonly Regex BoldPair = new(@"\*\*", RegexOptions.Compiled);
        private static readonly Regex HeadingRx = new(@"^#{1,6}\s+", RegexOptions.Compiled);
        private static readonly Regex RuleRx = new(@"^\s*(-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);
        private static readonly Regex BulletRx = new(@"^\s*[-*+]\s+", RegexOptions.Compiled);
        private static readonly Regex NumberedRx = new(@"^\s*\d{1,3}[.)]\s+", RegexOptions.Compiled);
        private static readonly Regex QuoteRx = new(@"^\s*>\s?", RegexOptions.Compiled);

        /// <summary>True when the line may open a fold: any prose or list
        /// line. Headings, rules, quotes and fences render on their own terms
        /// and never lend their text to the line above.</summary>
        internal static bool CanOpen(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }

            string trimmed = line.TrimStart();
            return !trimmed.StartsWith("```", StringComparison.Ordinal)
                && !RuleRx.IsMatch(line)
                && !HeadingRx.IsMatch(line)
                && !QuoteRx.IsMatch(line);
        }

        /// <summary>True when the line may fold INTO the one above: plain
        /// prose only. A blank, a fence, a heading, a rule, a list item or a
        /// quote below opens a new block, and an odd marker stays where it
        /// is - separate paragraphs, exactly what markdown promises.</summary>
        internal static bool CanFollow(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }

            string trimmed = line.TrimStart();
            return !trimmed.StartsWith("```", StringComparison.Ordinal)
                && !RuleRx.IsMatch(line)
                && !HeadingRx.IsMatch(line)
                && !BulletRx.IsMatch(line)
                && !NumberedRx.IsMatch(line)
                && !QuoteRx.IsMatch(line);
        }

        /// <summary>The line's ** marker pairs: odd means a bold left open -
        /// the fold's only trigger. Single asterisks are nobody's emergency:
        /// they are multiplication and footnotes far more often than italics.</summary>
        internal static int BoldPairCount(string line) =>
            string.IsNullOrEmpty(line) ? 0 : BoldPair.Matches(line).Count;

        /// <summary>Folds soft-broken lines back together wherever a bold
        /// span straddles the wrap. The join is the space the soft break
        /// stands for, or a direct abutment when both boundaries are CJK (a
        /// space between Chinese sentences is a typo). Returns the same
        /// array when nothing is unbalanced - the common case pays one
        /// count per line and stops.</summary>
        public static string[] Fold(string[] lines)
        {
            if (lines.Length < 2)
            {
                return lines;
            }

            var merged = new List<string>(lines);
            bool inFence = false;
            for (int i = 0; i < merged.Count - 1; i++)
            {
                if (merged[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;     // the same law the builder obeys
                    continue;
                }

                if (inFence)
                {
                    continue;               // fenced code renders verbatim
                }

                int guard = 0;
                while (guard++ < 12
                       && i + 1 < merged.Count
                       && CanOpen(merged[i])
                       && BoldPairCount(merged[i]) % 2 == 1
                       && CanFollow(merged[i + 1]))
                {
                    merged[i] = Join(merged[i], merged[i + 1]);
                    merged.RemoveAt(i + 1);
                }
            }

            return merged.ToArray();
        }

        // The soft-break join: the two halves pressed together, separated
        // by the one space the break stands for - two CJK boundaries abut
        // directly (a space between Chinese sentences is a typo).
        private static string Join(string prev, string next)
        {
            bool prevCjk = prev.Length > 0 && IsCjk(prev[^1]);
            bool nextCjk = next.Length > 0 && IsCjk(next[0]);
            return prevCjk && nextCjk ? prev + next : prev + " " + next;
        }

        private static bool IsCjk(char c) =>
            (c >= '\u2E80' && c <= '\u9FFF') || (c >= '\uAC00' && c <= '\uD7AF') || c >= '\uF900';
    }
}
