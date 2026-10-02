// Features/Summary/MarkdownNormalizer.cs - geometric Markdown extraction.
//
// The old extraction handed the models raw visual line dumps: every printed
// line ended with a hard newline, margin hyphens split words ("civili-" /
// "zation"), and headings rode inside [[H]] ... [[/H]] wrappers. On a
// 100-page book that scaffolding alone wasted 10-15k tokens and cut every
// sentence into fragments. This normalizer renders the SAME geometry
// (TextRunService reading-order runs) as compact GitHub-Flavored Markdown:
//   * De-hyphenation: a line ending in letter+hyphen whose next line starts
//     with a lowercase letter merges the word ("devel-" + "opment" ->
//     "development") with no space and no hyphen.
//   * Paragraph reflow: lines join with a single space unless the previous
//     line ended a sentence AND the vertical gap exceeds normal leading
//     (> 1.3x the page's median line gap), or the next line indents, or a
//     heading/bullet starts - those emit real paragraph breaks (\n\n).
//   * Native markdown headings: the book's printed headings, still detected
//     by font geometry against MedianBodyPointSize, render as # / ## / ###
//     by size tier (>= 1.50x, >= 1.25x, >= 1.15x the median body size).
//   * Bullet normalization: line-initial bullet glyphs convert to "- " list
//     items; a wrapped item's continuation lines stay inside their item.
//   * Compact page anchors: the caller frames each page with "[p. N]".
// The heading candidates still flow to the running-head filter exactly as
// before; only the rendered surface changed. No WPF, no I/O - the class is
// unit-testable against synthetic PageTextRuns.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using Avalanche.Services;

    internal static class MarkdownNormalizer
    {
        // Heading tiers, relative to MedianBodyPointSize. 1.15 stays the
        // detection floor (the old HeadingSizeRatio): anything smaller never
        // counts as a heading, and the tiers split the detected headings into
        // Title/Section/Subsection marks.
        public const double HeadingTier1Ratio = 1.50;   // '#'
        public const double HeadingTier2Ratio = 1.25;   // '##'
        public const double HeadingSizeRatio = 1.15;    // '###' (detection floor)
        public const int HeadingMaxChars = 90;

        // A paragraph break needs a real typographic pause: the previous line
        // ended a sentence and the vertical gap clears 1.3x normal leading.
        private const double ParagraphGapRatio = 1.3;

        /// <summary>Renders one page's text layer as compact GFM. Heading
        /// candidates are appended to <paramref name="candidates"/> for the
        /// caller's running-head filter (unchanged contract).</summary>
        public static string BuildPageMarkdown(
            PageTextRuns runs, int page, List<(int Page, string Norm)> candidates)
        {
            double median = MedianBodyPointSize(runs);
            if (runs.Lines.Count == 0)
            {
                return string.Empty;
            }

            // Pages whose text layer reports one font size for everything
            // (some OCR output) cannot tell headings from body: raw dump,
            // exactly the old fallback.
            if (median <= 0)
            {
                return TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _);
            }

            int n = runs.Lines.Count;
            var texts = new string[n];
            for (int i = 0; i < n; i++)
            {
                texts[i] = TextRunService.TextForRange(runs, runs.Lines[i].Start, runs.Lines[i].End, out _);
            }

            double leading = MedianLineGap(runs.Lines);
            if (leading <= 0)
            {
                leading = median;   // degenerate gap metric: the font size is the sane floor
            }

            double baseLeft = MedianLineLeft(runs.Lines);

            var sb = new StringBuilder();
            bool first = true;      // nothing emitted yet on this page
            bool paraOpen = false;  // a prose paragraph is on the run
            bool listOpen = false;  // a "- " bullet list is on the run
            string prevText = string.Empty;
            RunLine? prevLine = null;

            for (int i = 0; i < n; i++)
            {
                // ---- headings: consecutive heading-sized lines merge (wrapped titles)
                if (IsHeadingLine(runs, runs.Lines[i], median))
                {
                    int j = i;
                    var parts = new List<string>();
                    double bestRatio = 0;
                    while (j < n && IsHeadingLine(runs, runs.Lines[j], median))
                    {
                        string t = texts[j].Trim();
                        if (t.Length > 0)
                        {
                            parts.Add(t);
                            bestRatio = Math.Max(bestRatio, MaxGlyphSize(runs, runs.Lines[j]) / median);
                        }

                        j++;
                    }

                    string heading = string.Join(" ", parts).Trim();
                    if (heading.Length > 0)
                    {
                        paraOpen = false;
                        listOpen = false;
                        if (!first)
                        {
                            sb.Append("\n\n");
                        }

                        sb.Append(HeadingMarks(bestRatio)).Append(' ').Append(heading);
                        candidates.Add((page, NormalizeHeading(heading)));
                        first = false;
                        prevLine = null;
                        prevText = string.Empty;
                        i = j - 1;   // the for-loop advances past the merged run
                        continue;
                    }
                }

                string text = texts[i].Trim();
                if (text.Length == 0)
                {
                    continue;   // blank visual line: geometry decides the breaks
                }

                RunLine line = runs.Lines[i];
                bool breaks = prevLine is null || BreaksBlock(
                    prevLine, prevText, line, median, leading, baseLeft);

                if (TryStripBullet(text, out string item))
                {
                    paraOpen = false;
                    // A line that carries a bullet glyph always opens its own
                    // item - a wrapped item's continuation is the glyphless
                    // line below, never another glyph.
                    if (listOpen)
                    {
                        sb.Append("\n");   // another item in the open list
                    }
                    else
                    {
                        if (!first)
                        {
                            sb.Append("\n\n");
                        }

                        listOpen = true;
                    }

                    sb.Append("- ").Append(item);
                }
                else if (listOpen && !breaks)
                {
                    // Inside an open list, a plain line that does not start a
                    // new block keeps the current item whole.
                    AppendContinuation(sb, prevText, text);
                }
                else if (paraOpen && !breaks)
                {
                    AppendContinuation(sb, prevText, text);
                }
                else
                {
                    listOpen = false;
                    paraOpen = true;
                    if (!first)
                    {
                        sb.Append("\n\n");
                    }

                    sb.Append(text);
                }

                first = false;
                prevLine = line;
                prevText = text;
            }

            return sb.ToString();
        }

        // ---- block boundaries -------------------------------------------------

        /// <summary>Does <paramref name="cur"/> open a new block after
        /// <paramref name="prev"/>? A paragraph break needs one of: an indent
        /// deeper than both the page's base left and the previous line, or a
        /// sentence-ending previous line whose vertical gap clears normal
        /// leading. Everything else reflows with a single space.</summary>
        private static bool BreaksBlock(
            RunLine prev, string prevText, RunLine cur,
            double median, double leading, double baseLeft)
        {
            double indentRef = Math.Max(baseLeft, prev.Left);
            if (cur.Left - indentRef > Math.Max(6.0, 0.6 * median))
            {
                return true;   // first-line indent of a new paragraph
            }

            if (EndsSentence(prevText) &&
                Gap(prev, cur) > ParagraphGapRatio * leading)
            {
                return true;   // sentence end + a real typographic pause
            }

            return false;
        }

        /// <summary>Appends a continuation line to the open paragraph or list
        /// item: de-hyphenated (hyphen dropped, no space) when the previous
        /// line ended a letter-hyphen split, single space otherwise.</summary>
        private static void AppendContinuation(StringBuilder sb, string prevText, string text)
        {
            if (HyphenJoin(prevText, text))
            {
                sb.Length--;                 // drop the trailing '-'
                sb.Append(text);
            }
            else
            {
                sb.Append(' ').Append(text);
            }
        }

        /// <summary>"devel-" + "opment": a letter directly before the hyphen and
        /// a lowercase opener on the next line is one word the typographer split.</summary>
        private static bool HyphenJoin(string prev, string cur) =>
            prev.Length >= 2 && prev[^1] == '-' && char.IsLetter(prev[^2]) &&
            cur.Length > 0 && char.IsLower(cur[0]);

        /// <summary>The vertical pause between two lines (PDF space: Top > Bottom,
        /// the next line's Top sits below the previous line's Bottom).</summary>
        private static double Gap(RunLine prev, RunLine cur) =>
            Math.Max(0, prev.Bottom - cur.Top);

        /// <summary>Sentence-ending punctuation, tolerating a closing quote or
        /// bracket before the mark. ':' counts: it precedes lists and extracts.</summary>
        private static bool EndsSentence(string text)
        {
            int end = text.Length;
            while (end > 0 && (text[end - 1] == '"' || text[end - 1] == '\'' ||
                               text[end - 1] == ')' || text[end - 1] == ']' ||
                               text[end - 1] == '\u201d' || text[end - 1] == '\u2019'))
            {
                end--;
            }

            return end > 0 && (text[end - 1] is '.' or '!' or '?' or ':');
        }

        // ---- bullets -----------------------------------------------------------

        /// <summary>Line-initial bullet glyphs (spec set: bullet, en-dash, square,
        /// asterisk) become "- " items. A math asterisk ("*0.5", "*=") is not a
        /// bullet; an empty tail is not one either.</summary>
        private static bool TryStripBullet(string text, out string item)
        {
            item = string.Empty;
            if (text.Length < 2 || (text[0] is not ('\u2022' or '\u2013' or '\u25aa' or '*')))
            {
                return false;
            }

            string tail = text[1..].TrimStart();
            if (tail.Length == 0)
            {
                return false;
            }

            if (text[0] == '*' && (char.IsDigit(tail[0]) || tail[0] is '=' or '/' or ' '))
            {
                return false;
            }

            item = tail;
            return true;
        }

        // ---- headings (the book's OWN printed headings) ------------------------
        //
        // Detection is unchanged: a printed heading is a short line whose largest
        // glyph is clearly bigger than the page's median body font. Consecutive
        // heading-sized lines merge (a wrapped title spans two bands). Pages whose
        // text layer reports one font size for everything yield no headings at all.

        private static bool IsHeadingLine(PageTextRuns runs, RunLine line, double medianBody)
        {
            double max = MaxGlyphSize(runs, line);
            int letters = 0;
            for (int i = line.Start; i < line.End; i++)
            {
                RunChar c = runs.Chars[i];
                if (c.Value.Length > 0 && char.IsLetter(c.Value[0]))
                {
                    letters++;
                }
            }

            if (max <= 0 || letters < 2 || max < medianBody * HeadingSizeRatio)
            {
                return false;
            }

            string text = TextRunService.TextForRange(runs, line.Start, line.End, out _).Trim();
            if (text.Length == 0 || text.Length > HeadingMaxChars)
            {
                return false;
            }

            char last = text[text.Length - 1];
            return last is not ('.' or ',' or ';' or ':' or '!' or '?');
        }

        private static double MaxGlyphSize(PageTextRuns runs, RunLine line)
        {
            double max = 0;
            for (int i = line.Start; i < line.End; i++)
            {
                RunChar c = runs.Chars[i];
                if (c.PointSize > max)
                {
                    max = c.PointSize;
                }
            }

            return max;
        }

        private static string HeadingMarks(double ratio) =>
            ratio >= HeadingTier1Ratio ? "#" :
            ratio >= HeadingTier2Ratio ? "##" : "###";

        private static double MedianBodyPointSize(PageTextRuns runs)
        {
            var sizes = new List<double>();
            foreach (RunChar c in runs.Chars)
            {
                if (c.PointSize > 0 && c.Value.Length > 0 && char.IsLetter(c.Value[0]))
                {
                    sizes.Add(c.PointSize);
                }
            }

            if (sizes.Count == 0)
            {
                return 0;
            }

            sizes.Sort();
            return sizes[sizes.Count / 2];
        }

        /// <summary>Median vertical gap between consecutive lines: the page's
        /// "normal leading" the paragraph-break rule measures against.</summary>
        private static double MedianLineGap(IReadOnlyList<RunLine> lines)
        {
            var gaps = new List<double>();
            for (int i = 1; i < lines.Count; i++)
            {
                double gap = lines[i - 1].Bottom - lines[i].Top;
                if (gap > 0)
                {
                    gaps.Add(gap);
                }
            }

            return Median(gaps);
        }

        private static double MedianLineLeft(IReadOnlyList<RunLine> lines)
        {
            var lefts = new List<double>();
            foreach (RunLine line in lines)
            {
                lefts.Add(line.Left);
            }

            return Median(lefts);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0)
            {
                return 0;
            }

            values.Sort();
            return values[values.Count / 2];
        }

        // ---- running heads (page furniture) -------------------------------------

        private static string NormalizeHeading(string s)
        {
            var sb = new StringBuilder();
            bool space = true;
            foreach (char ch in s)
            {
                if (char.IsLetter(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    space = false;
                }
                else if (!space)
                {
                    sb.Append(' ');
                    space = true;
                }
            }

            return sb.ToString().Trim();
        }

        /// <summary>Heading candidates repeat across many pages = page furniture
        /// (book/chapter running head, page-number header), never a section title.
        /// The cap keeps big ranges honest, the floor keeps single strays alive:
        /// a head seen on 3+ pages of the range is furniture.</summary>
        public static HashSet<string> DetectRunningHeads(
            List<(int Page, string Norm)> candidates, int pageCount)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (pageCount >= 5 && candidates.Count > 0)
            {
                var byText = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
                foreach ((int page, string norm) in candidates)
                {
                    if (!byText.TryGetValue(norm, out var pages))
                    {
                        byText[norm] = pages = new HashSet<int>();
                    }

                    pages.Add(page);
                }

                int threshold = Math.Max(3, Math.Min((int)Math.Ceiling(pageCount * 0.15), 5));
                foreach (var (norm, pages) in byText)
                {
                    if (pages.Count >= threshold)
                    {
                        result.Add(norm);
                    }
                }
            }

            return result;
        }

        /// <summary>Drops markdown heading lines (and any legacy [[H]]-wrapped
        /// ones) whose normalized text is a detected running head.</summary>
        public static string StripRunningHeads(string pageText, HashSet<string> runningHeads)
        {
            if (runningHeads.Count == 0)
            {
                return pageText;
            }

            var kept = new List<string>();
            foreach (string line in pageText.Split('\n'))
            {
                string trimmed = line.Trim();
                string? inner = MarkdownHeadingText(trimmed) ?? LegacyHeadingText(trimmed);
                if (inner is not null && runningHeads.Contains(NormalizeHeading(inner)))
                {
                    continue;
                }

                kept.Add(line);
            }

            return string.Join("\n", kept);
        }

        /// <summary>"## The Rise of Cities" -> "The Rise of Cities"; null when the
        /// line is not a markdown heading.</summary>
        private static string? MarkdownHeadingText(string line)
        {
            int k = 0;
            while (k < line.Length && line[k] == '#')
            {
                k++;
            }

            if (k is >= 1 and <= 6 && k < line.Length && line[k] == ' ')
            {
                string inner = line[(k + 1)..].Trim();
                return inner.Length > 0 ? inner : null;
            }

            return null;
        }

        private static string? LegacyHeadingText(string line) =>
            line.StartsWith("[[H]]", StringComparison.Ordinal) &&
            line.EndsWith("[[/H]]", StringComparison.Ordinal) &&
            line.Length > 11
                ? line[5..^6].Trim()
                : null;
    }
}
