using Avalanche.Features.AI;
using UglyToad.PdfPig;

namespace Avalanche.Services
{
    internal sealed class SearchResult
    {
        public Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> PageRects { get; } = [];
        public List<int> ResultPages { get; } = [];
        public int TotalHits { get; set; }
    }

    internal sealed class SearchService
    {
        /// <summary>
        /// Scans every page of <paramref name="filePath"/> for <paramref name="query"/> (case-insensitive).
        /// Returns an empty result when query is blank or the file cannot be opened.
        /// </summary>
        public static SearchResult Search(string filePath, string query)
        {
            var result = new SearchResult();
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(filePath))
                return result;

            try
            {
                using var doc = PdfDocument.Open(filePath);
                for (int pi = 0; pi < doc.NumberOfPages; pi++)
                {
                    var page = doc.GetPage(pi + 1);
                    var hits = FindMatchesOnPage(page, query);
                    if (hits.Count > 0)
                    {
                        result.PageRects[pi] = hits;
                        result.ResultPages.Add(pi);
                        result.TotalHits += hits.Count;
                    }
                }
            }
            catch { /* return whatever was collected so far */ }

            return result;
        }

        internal static List<(double Left, double Bottom, double Right, double Top)> FindMatchesOnPage(
            UglyToad.PdfPig.Content.Page page, string query)
        {
            var result = new List<(double, double, double, double)>();
            var words = page.GetWords().ToList();

            // A multi-word phrase can span adjacent words; a single token never does, so for single-token
            // queries we only do the per-word substring match (the cross-word union box below would
            // otherwise highlight whole runs of words leading up to the matching one).
            bool isPhrase = query.Trim().Contains(' ');

            for (int i = 0; i < words.Count; i++)
            {
                if (words[i].Text.Contains(query, System.StringComparison.OrdinalIgnoreCase))
                {
                    var bb = words[i].BoundingBox;
                    result.Add((bb.Left, bb.Bottom, bb.Right, bb.Top));
                    continue;
                }

                if (!isPhrase) continue;

                // Multi-word match
                string combined = words[i].Text;
                for (int j = i + 1; j < words.Count && combined.Length < query.Length + 20; j++)
                {
                    combined += " " + words[j].Text;
                    if (combined.Contains(query, System.StringComparison.OrdinalIgnoreCase))
                    {
                        double minX = double.MaxValue, minY = double.MaxValue;
                        double maxX = double.MinValue, maxY = double.MinValue;
                        for (int k = i; k <= j; k++)
                        {
                            var wbb = words[k].BoundingBox;
                            minX = Math.Min(minX, wbb.Left);
                            minY = Math.Min(minY, wbb.Bottom);
                            maxX = Math.Max(maxX, wbb.Right);
                            maxY = Math.Max(maxY, wbb.Top);
                        }
                        result.Add((minX, minY, maxX, maxY));
                        break;
                    }
                }
            }
            return result;
        }

        // ── AI citation passage location ─────────────────────────────────────────────
        // The AI sidebar's citation highlight needs the page coordinates of the passage a
        // citation refers to. This locator reuses the SAME word geometry every other text
        // feature uses (PdfPig GetWords - search highlights, the chunk indexer, TextRunService),
        // so a located passage paints exactly where search highlights paint.

        /// <summary>
        /// Locates a passage (an AI chunk's per-page text, or the model's quote) among a
        /// page's words and returns one union rect per visual line segment the passage
        /// covers, in the same PDF space FindMatchesOnPage returns. The caller paints a
        /// highlighter-style band over each line. Match passes go strict to loose: exact
        /// word sequence, punctuation/ligature-relaxed sequence (lowercase letters+digits
        /// only, so curly quotes and soft hyphens cannot break it), then a fuzzy in-order
        /// run that tolerates a few needle words the page lacks. Null when nothing
        /// sensible matched.
        /// </summary>
        internal static PassageMatch? LocatePassage(UglyToad.PdfPig.Content.Page page, string needle)
            => LocatePassageWords(page.GetWords().Select(ToPassageWord).ToList(), needle);

        /// <summary>
        /// Tries each needle in order against one page, opening the file once. Returns the
        /// first match - callers pass candidates most-faithful-first (chunk page slice,
        /// model quote, prefix/suffix windows).
        ///
        /// Two passes over the same open document: first the raw content-stream word
        /// order (single-column files, and everything the AI highlighter matched before
        /// column-aware chunking existed), then - only when nothing matched - the page's
        /// words rebuilt in the same column-aware reading order the AI indexer chunks
        /// with. Chunk page slices are cut from that reading order, so on multi-column
        /// pages they cannot appear contiguously in content-stream order and only this
        /// second pass can find them.
        /// </summary>
        internal static PassageMatch? LocatePassageInFile(string filePath, int pageIndex, IEnumerable<string> needles)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            try
            {
                using var doc = PdfDocument.Open(filePath);
                if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return null;
                var page = doc.GetPage(pageIndex + 1);
                var words = page.GetWords().Select(ToPassageWord).ToList();
                if (words.Count == 0) return null;

                var active = needles.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                foreach (var needle in active)
                {
                    var match = LocatePassageWords(words, needle);
                    if (match is not null) return match;
                }

                // Reading-order retry. The stream builder emits every word exactly
                // once, so any needle that exists on the page is findable here.
                var raw = page.GetWords()
                    .Select(w => new IndexedWord(w.Text, w.BoundingBox.Left, w.BoundingBox.Bottom,
                        w.BoundingBox.Right, w.BoundingBox.Top))
                    .ToList();
                var stream = PageWordStreamBuilder.Build(raw, page.Width, page.Height);
                if (stream.Words.Count == 0) return null;
                var ordered = stream.Words
                    .Select(w => new PassageWord(w.Text, w.Left, w.Bottom, w.Right, w.Top))
                    .ToList();

                foreach (var needle in active)
                {
                    var match = LocatePassageWords(ordered, needle);
                    if (match is not null) return match;
                }
            }
            catch { /* encrypted/broken file - no passage location on this page */ }
            return null;
        }

        /// <summary>Text plus PDF-space box the passage locator works on - lets PdfPig
        /// words and reading-order IndexedWords share one matching core.</summary>
        private readonly record struct PassageWord(string Text, double Left, double Bottom, double Right, double Top);

        private static PassageWord ToPassageWord(UglyToad.PdfPig.Content.Word w)
            => new(w.Text, w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top);

        private static PassageMatch? LocatePassageWords(List<PassageWord> words, string needle)
        {
            if (words.Count == 0 || string.IsNullOrWhiteSpace(needle)) return null;

            var needleWords = needle.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (needleWords.Length == 0) return null;

            // Pass A: exact word sequence (case-insensitive). A chunk page slice was built
            // from these very words, so a faithful index matches here.
            var rawItems = words.Select(w => w.Text).ToList();
            (int Start, int End)? hit = SequencePass(rawItems, needleWords, StringComparer.OrdinalIgnoreCase);
            if (hit is { } exact)
                return BuildLineRects(words, exact.Start, exact.End - exact.Start);

            // Pass B/C: punctuation/ligature-relaxed. Empty normalizations (punctuation-only
            // tokens like a bare em dash) are dropped from BOTH sides; normIdx maps relaxed
            // positions back to the original words so geometry stays exact.
            var normItems = new List<string>(words.Count);
            var normIdx = new List<int>(words.Count);
            for (int i = 0; i < words.Count; i++)
            {
                var n = NormalizeWordForPassage(words[i].Text);
                if (n.Length == 0) continue;
                normItems.Add(n);
                normIdx.Add(i);
            }
            if (normItems.Count == 0) return null;

            var needleNorm = needleWords
                .Select(NormalizeWordForPassage)
                .Where(s => s.Length > 0)
                .ToArray();
            if (needleNorm.Length == 0) return null;

            var relaxed = SequencePass(normItems, needleNorm, StringComparer.Ordinal)
                          ?? FuzzyRunPass(normItems, needleNorm);
            if (relaxed is not { } r || r.End <= r.Start || r.End > normIdx.Count) return null;

            int origStart = normIdx[r.Start];
            int origEnd = normIdx[r.End - 1];
            return BuildLineRects(words, origStart, origEnd - origStart + 1);
        }

        /// <summary>Consecutive word-sequence equality: items[i..i+n) must equal the needle
        /// words one-for-one. O(page words x needle words) with cheap comparisons. Returns
        /// the half-open item range [Start, End) the needle covers.</summary>
        private static (int Start, int End)? SequencePass(List<string> items, string[] needleWords, StringComparer comparer)
        {
            int n = needleWords.Length;
            for (int i = 0; i + n <= items.Count; i++)
            {
                bool ok = true;
                for (int k = 0; k < n; k++)
                {
                    if (!comparer.Equals(items[i + k], needleWords[k])) { ok = false; break; }
                }
                if (ok) return (i, i + n);
            }
            return null;
        }

        /// <summary>Longest in-order run of needle words found consecutively on the page,
        /// tolerating a few mismatched words (quotes that drop a word, or page words the
        /// needle lacks). On a mismatch the walk skips whichever side lands directly on
        /// the next match, budget-limited. Returns the half-open PAGE word range [Start,
        /// End) the run covers - skipped page words sit inside it and stay highlighted,
        /// so the rect always covers one contiguous page passage. Accepted when the run
        /// reaches at least four words, or a quarter of a longer needle.</summary>
        private static (int Start, int End)? FuzzyRunPass(List<string> items, string[] needleWords)
        {
            int maxSkips = Math.Max(1, needleWords.Length / 6);
            int bestStart = -1, bestMatched = 0, bestEnd = -1;
            for (int i = 0; i < items.Count; i++)
            {
                int matched = 0, skips = 0, p = i, k = 0;
                while (p < items.Count && k < needleWords.Length)
                {
                    if (items[p] == needleWords[k]) { p++; k++; matched++; continue; }
                    if (skips >= maxSkips) break;
                    // Skip whichever side (page word not in the needle, or needle word not
                    // on the page) lands directly on the next match.
                    bool needleSkipMatches = k + 1 < needleWords.Length && items[p] == needleWords[k + 1];
                    bool pageSkipMatches = p + 1 < items.Count && items[p + 1] == needleWords[k];
                    if (pageSkipMatches && !needleSkipMatches) p++;
                    else k++;
                    skips++;
                }
                if (matched > bestMatched) { bestMatched = matched; bestStart = i; bestEnd = p; }
                if (bestMatched >= needleWords.Length) break;
            }

            int minRun = Math.Min(needleWords.Length, Math.Max(4, needleWords.Length / 4));
            if (bestStart < 0 || bestMatched < minRun) return null;
            return (bestStart, bestEnd);
        }

        private static string NormalizeWordForPassage(string word)
        {
            var sb = new System.Text.StringBuilder(word.Length);
            foreach (char ch in word)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>Groups the matched words into visual lines (vertical-band overlap, the
        /// same rule TextRunService uses), splits lines on wide gaps (column gutters,
        /// table seams) and unions each segment into one rect.</summary>
        private static PassageMatch? BuildLineRects(List<PassageWord> words, int start, int count)
        {
            if (start < 0 || count <= 0 || start + count > words.Count) return null;

            var match = new PassageMatch { MatchedWords = count };
            var sel = words.Skip(start).Take(count).ToList();

            var lines = new List<(double Top, double Bottom, List<PassageWord> Words)>();
            foreach (var w in sel)
            {
                double t = w.Top, b = w.Bottom;
                int found = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    double overlap = Math.Min(lines[i].Top, t) - Math.Max(lines[i].Bottom, b);
                    double minH = Math.Min(lines[i].Top - lines[i].Bottom, t - b);
                    if (minH > 0 && overlap >= minH * 0.5) { found = i; break; }
                }
                if (found < 0)
                    lines.Add((t, b, new List<PassageWord> { w }));
                else
                {
                    var (lt, lb, lw) = lines[found];
                    lw.Add(w);
                    lines[found] = (Math.Max(lt, t), Math.Min(lb, b), lw);
                }
            }

            // A wide horizontal gap inside a line means a column gutter or a table seam -
            // words on either side get their own rect instead of one band sweeping the
            // whitespace between the columns.
            double gapT = Math.Max(15, MedianWordHeight(sel) * 2.0);

            foreach (var (_, _, ws) in lines)
            {
                var ordered = ws.OrderBy(w => w.Left).ToList();
                int segStart = 0;
                for (int i = 1; i <= ordered.Count; i++)
                {
                    bool flush = i == ordered.Count;
                    if (!flush)
                        flush = ordered[i].Left - ordered[i - 1].Right > gapT;
                    if (!flush) continue;

                    double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
                    for (int k = segStart; k < i; k++)
                    {
                        l = Math.Min(l, ordered[k].Left);
                        b = Math.Min(b, ordered[k].Bottom);
                        r = Math.Max(r, ordered[k].Right);
                        t = Math.Max(t, ordered[k].Top);
                    }
                    match.LineRects.Add((l, b, r, t));
                    segStart = i;
                }
            }

            return match.LineRects.Count > 0 ? match : null;
        }

        private static double MedianWordHeight(List<PassageWord> words)
        {
            var hs = words
                .Select(w => w.Top - w.Bottom)
                .Where(h => h > 0)
                .OrderBy(h => h)
                .ToList();
            return hs.Count == 0 ? 10 : hs[hs.Count / 2];
        }
    }
}

/// <summary>A located passage: one union rect per visual line segment (PDF space,
/// bottom-left origin) plus the number of page words the match covers.</summary>
internal sealed class PassageMatch
{
    public List<(double Left, double Bottom, double Right, double Top)> LineRects { get; } = [];
    public int MatchedWords { get; set; }
}
