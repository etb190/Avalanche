using System;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// One web page's readable text, extracted fresh from the live browser tab
    /// for the sidechat (v1.19.22). Nothing here is persisted: the snapshot is
    /// built when a question is sent, used for that one answer, and dropped -
    /// a page cannot leave a cached trace behind to collide with another page.
    /// </summary>
    public sealed class WebPageSnapshot
    {
        public string Url { get; init; } = "";
        public string Title { get; init; } = "";
        public string Byline { get; init; } = "";
        public string SiteName { get; init; } = "";
        public string Text { get; init; } = "";

        /// <summary>True when the text came from Readability's article body;
        /// false when the raw visible-text fallback had to answer instead
        /// (webapps and SPAs the article scorer cannot classify).</summary>
        public bool IsArticleExtraction { get; init; }

        /// <summary>True when the page's text was longer than the context
        /// budget and only its head and tail were kept.</summary>
        public bool Truncated { get; init; }
    }

    /// <summary>The citation jump's verdict, parsed from the finder
    /// script's JSON: whether the passage was found (and selected), and
    /// whether the custom highlight painted on top of the selection.</summary>
    public sealed class WebHighlightResult
    {
        public bool Ok { get; init; }
        public string Reason { get; init; } = "";
        public bool Painted { get; init; }
    }

    /// <summary>
    /// The browser sidechat's pure logic (v1.19.22): the web session key
    /// prefix, the extraction result parse, the page text budget and the
    /// page-context system prompt. Kept free of WPF and HTTP so the whole
    /// class can be audited (and tested) on its own.
    /// </summary>
    public static class WebChat
    {
        /// <summary>Web tab session keys live under this prefix - deliberately
        /// disjoint from the PDF keyspace ("doc_...") that DocumentIndexer
        /// computes, so a page and a book can never resolve to the same chat
        /// history, the same index or the same anything.</summary>
        public const string SessionPrefix = "wt_";

        public static bool IsWebSessionKey(string key)
            => !string.IsNullOrEmpty(key) && key.StartsWith(SessionPrefix, StringComparison.Ordinal);

        /// <summary>The chat-history key for one browser tab. Tabs arrive with
        /// their own "wt_..." id; the prefix is added (once) for any caller
        /// that hands in a bare id.</summary>
        public static string SessionKey(string tabId)
            => string.IsNullOrEmpty(tabId) ? "" :
               tabId.StartsWith(SessionPrefix, StringComparison.Ordinal) ? tabId : SessionPrefix + tabId;

        // The page text budget. A long page rides to the model as its head and
        // its tail with a cut marker between - the reader asked about the page
        // they see, and both ends of it are usually what that means. 160k chars
        // is a fraction of the single-pass budget the summarizer already sends
        // whole, so the sidechat's call stays light on every endpoint.
        public const int MaxPageChars = 160_000;
        public const int HeadChars = 140_000;
        public const int TailChars = 20_000;

        public const string TruncationMarker =
            "\n\n[The page's text was cut here to fit the context window: the beginning and the end are shown, the middle left out.]\n\n";

        /// <summary>Clamps the page text to the budget: short text passes
        /// through untouched, long text survives as head + marker + tail.</summary>
        public static (string Text, bool Truncated) ClampPageText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxPageChars)
                return (text ?? "", false);
            string head = text.Substring(0, HeadChars);
            string tail = text.Substring(text.Length - TailChars);
            return (head + TruncationMarker + tail, true);
        }

        /// <summary>
        /// Parses one ExecuteScriptAsync result into a snapshot. The engine
        /// returns the script's string as a JSON string literal, so the result
        /// may be double-encoded (a JSON string holding JSON) - both shapes are
        /// accepted. Returns null whenever the page offered nothing readable:
        /// a scheme the extractor refuses (browser/error pages), a PDF viewer,
        /// or simply no text. Null is the caller's "no page here" answer.
        /// </summary>
        public static WebPageSnapshot? ParseExtractionResult(string? executeScriptResult)
        {
            if (string.IsNullOrWhiteSpace(executeScriptResult))
                return null;

            string? inner = null;
            // Shape 1: a JSON string literal (the engine's own encoding). The
            // probe never touches the reflection-based serializer - a plain
            // JsonDocument parse answers "string or object?" on any input.
            try
            {
                using var probe = System.Text.Json.JsonDocument.Parse(executeScriptResult);
                if (probe.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    inner = probe.RootElement.GetString();
            }
            catch
            {
                // Not valid JSON in any shape: the payload below fails the
                // same way and the caller gets its null.
            }

            string payload = inner ?? executeScriptResult;
            if (string.IsNullOrWhiteSpace(payload) || payload == "null")
                return null;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !TryGetBool(root, "ok", out bool ok) || !ok)
                    return null;
                if (!TryGetString(root, "text", out var text) || string.IsNullOrWhiteSpace(text))
                    return null;

                text = text.Trim();
                if (text.Length == 0)
                    return null;

                TryGetString(root, "mode", out var modeStr);
                string mode = modeStr ?? "";
                var (clamped, truncated) = ClampPageText(text);

                return new WebPageSnapshot
                {
                    Url = TryGetString(root, "url", out var url) ? url ?? "" : "",
                    Title = TryGetString(root, "title", out var title) ? title ?? "" : "",
                    Byline = TryGetString(root, "byline", out var byline) ? byline ?? "" : "",
                    SiteName = TryGetString(root, "site", out var site) ? site ?? "" : "",
                    Text = clamped,
                    IsArticleExtraction = string.Equals(mode, "readability", StringComparison.Ordinal),
                    Truncated = truncated
                };
            }
            catch
            {
                return null;   // anything unreadable is simply no page
            }
        }

        private static bool TryGetString(System.Text.Json.JsonElement root, string name, out string? value)
        {
            value = null;
            if (!root.TryGetProperty(name, out var prop)
                || prop.ValueKind != System.Text.Json.JsonValueKind.String)
                return false;
            value = prop.GetString();
            return value is not null;
        }

        private static bool TryGetBool(System.Text.Json.JsonElement root, string name, out bool value)
        {
            value = false;
            if (!root.TryGetProperty(name, out var prop))
                return false;
            if (prop.ValueKind == System.Text.Json.JsonValueKind.True) { value = true; return true; }
            if (prop.ValueKind == System.Text.Json.JsonValueKind.False) { value = false; return true; }
            return false;
        }

        /// <summary>
        /// The web sidechat's system prompt: the page itself is the evidence,
        /// split into numbered segments the model can cite. The citation
        /// dialect is the PDF prompt's own - inline [SOURCE_n] markers plus a
        /// JSON 'sources' list carrying exact quotes - so an answer's
        /// footnotes route their clicks back onto the live page (v1.19.25):
        /// the quote is what the browser searches for, selects, highlights
        /// and scrolls to. The contract stays JSON so the provider's tolerant
        /// parser handles both a structured and a plain-prose reply.
        /// </summary>
        public static string BuildSystemPrompt(WebPageSnapshot page)
        {
            // The full first-turn prompt: the instruction head, the page
            // evidence, the citation contract - one string (v1.19.93).
            var sb = new System.Text.StringBuilder();
            sb.Append(InstructionHead());
            sb.AppendLine();
            sb.Append(BuildContextBlock(page));
            sb.AppendLine();
            sb.Append(CitationContract());
            return sb.ToString();
        }

        /// <summary>
        /// The page-independent half of the web sidechat's system prompt: the
        /// store's instruction voice, with NO page text. v1.19.93: the
        /// follow-up turns' prompt - the page was anchored once on the first
        /// turn and the rolling history carries the conversation since, so
        /// re-attaching the whole extraction on every turn (quadratic tokens,
        /// context limits blown on long pages) is over.
        /// </summary>
        public static string InstructionHead()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(AiPromptLibrary.WebSidechatHead());
            return sb.ToString();
        }

        /// <summary>
        /// The page's own evidence: the metadata head (with the extraction
        /// notes), then the numbered [SOURCE_n] segments the citation
        /// resolution hunts quotes through. Anchored once, on the
        /// conversation's first model call (v1.19.93).
        /// </summary>
        public static string BuildContextBlock(WebPageSnapshot page)
        {
            var segments = SplitPageSegments(page.Text);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("PAGE:");
            sb.Append("TITLE: ").AppendLine(string.IsNullOrEmpty(page.Title) ? "(untitled)" : page.Title);
            if (!string.IsNullOrWhiteSpace(page.Byline))
                sb.Append("AUTHOR: ").AppendLine(page.Byline);
            if (!string.IsNullOrWhiteSpace(page.SiteName))
                sb.Append("SITE: ").AppendLine(page.SiteName);
            sb.Append("URL: ").AppendLine(string.IsNullOrEmpty(page.Url) ? "(unknown)" : page.Url);
            if (!page.IsArticleExtraction)
                sb.AppendLine("NOTE: the page resisted article extraction, so the text below is everything visible on it (navigations and chrome included).");
            if (page.Truncated)
                sb.AppendLine("NOTE: the page is long; only its beginning and end are shown.");
            sb.AppendLine();
            sb.AppendLine("PAGE EVIDENCE:");
            sb.AppendLine();
            for (int i = 0; i < segments.Count; i++)
            {
                sb.AppendLine($"[SOURCE_{i + 1}]");
                sb.AppendLine(segments[i]);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>The JSON answer contract the provider's tolerant parser
        /// reads: the same words every turn, page-independent.</summary>
        public static string CitationContract()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Return ONLY a JSON object in this exact shape:");
            sb.AppendLine("{\"answer\": \"<your full answer with inline [SOURCE_n] markers>\", \"sources\": [{\"sourceId\": \"SOURCE_1\", \"quote\": \"<exact text copied from that segment>\", \"reason\": \"<why it supports the answer>\"}]}");
            sb.AppendLine("The 'quote' must be a short exact excerpt (up to ~300 characters) copied verbatim from the cited segment - it is used to find the passage on the live page.");
            sb.AppendLine("CRITICAL CITATION MANDATE - IMMEDIATE INLINE PLACEMENT: every factual claim, date, name, statistic, or finding carries its [SOURCE_n] marker IMMEDIATELY after that clause or sentence; NEVER cluster citations at the end of a sentence, paragraph, or answer.");
            return sb.ToString();
        }

        // ---- page segmentation for citations (v1.19.25) ----

        // Paragraphs longer than SegmentSplitMaxChars are cut on sentence
        // boundaries near the target size; pieces shorter than the floor
        // merge with a neighbor, so every segment carries enough text to be
        // worth citing. The cap keeps even a pathological page inside a
        // numbered evidence list a model can actually use.
        public const int SegmentTargetChars = 1200;
        public const int SegmentSplitMaxChars = 2400;
        public const int SegmentMinChars = 80;
        public const int MaxSegments = 160;

        /// <summary>Collapse every whitespace run to one space and trim -
        /// the shape quotes and page text are compared in (the model sees
        /// the snapshot's newlines; the live DOM wraps the same words).</summary>
        public static string NormalizeForMatch(string? text)
            => string.IsNullOrEmpty(text) ? ""
               : System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();

        /// <summary>
        /// Splits the page text into the numbered evidence segments the
        /// prompt offers as [SOURCE_n]. Blank-line paragraphs stay intact,
        /// oversized ones are cut on sentence boundaries, tiny ones merge
        /// into a neighbor, and the truncation marker never becomes
        /// evidence (the prompt carries the cut note itself). Deterministic:
        /// the same snapshot always produces the same numbering.
        /// </summary>
        public static IReadOnlyList<string> SplitPageSegments(string? pageText)
        {
            var segments = new List<string>();
            if (string.IsNullOrWhiteSpace(pageText))
                return segments;

            string marker = TruncationMarker.Trim();
            var paragraphs = pageText.Replace("\r\n", "\n")
                                     .Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            var pieces = new List<string>();
            foreach (var raw in paragraphs)
            {
                var p = raw.Trim();
                if (p.Length == 0 || string.Equals(p, marker, StringComparison.Ordinal))
                    continue;   // blank furniture, or the cut marker itself
                if (p.Length <= SegmentSplitMaxChars)
                {
                    pieces.Add(p);
                    continue;
                }

                int start = 0;
                while (start < p.Length)
                {
                    int remain = p.Length - start;
                    if (remain <= SegmentSplitMaxChars)
                    {
                        pieces.Add(p[start..].Trim());
                        break;
                    }

                    int cut = FindSentenceCut(p, start, start + SegmentTargetChars, start + SegmentSplitMaxChars);
                    string piece = p[start..cut].Trim();
                    if (piece.Length > 0)
                        pieces.Add(piece);
                    start = cut;
                    while (start < p.Length && char.IsWhiteSpace(p[start]))
                        start++;
                }
            }

            // Tiny pieces ride with a neighbor: a two-word line is not a
            // citation a reader can use. A trailing morsel joins the one
            // before it instead of vanishing forward.
            foreach (var piece in pieces)
            {
                if (segments.Count > 0 && segments[^1].Length < SegmentMinChars)
                    segments[^1] = segments[^1] + "\n\n" + piece;
                else
                    segments.Add(piece);
            }
            if (segments.Count > 1 && segments[^1].Length < SegmentMinChars)
            {
                segments[^2] = segments[^2] + "\n\n" + segments[^1];
                segments.RemoveAt(segments.Count - 1);
            }

            // Safety cap: pairwise-merge rounds until the count fits.
            while (segments.Count > MaxSegments)
            {
                var merged = new List<string>((segments.Count + 1) / 2);
                for (int i = 0; i < segments.Count; i += 2)
                    merged.Add(i + 1 < segments.Count
                        ? segments[i] + "\n\n" + segments[i + 1]
                        : segments[i]);
                segments = merged;
            }

            return segments;
        }

        /// <summary>The cut for an oversized paragraph: the last sentence
        /// ender at or before <paramref name="preferred"/>, else a hard cut
        /// there. Never returns <paramref name="start"/>.</summary>
        private static int FindSentenceCut(string text, int start, int preferred, int limit)
        {
            int hard = Math.Min(preferred, text.Length - 1);
            if (hard <= start)
                return Math.Min(start + 1, text.Length);
            for (int i = hard; i > start; i--)
            {
                char c = text[i - 1];
                if (c is '.' or '!' or '?' or '\u2026' or '\u3002' or '\uFF01' or '\uFF1F')
                    return i;
            }

            return hard;
        }

        /// <summary>
        /// Maps each SOURCE_n id a WEB reply returned back to the n-th
        /// segment of the page snapshot (the same numbering the prompt
        /// offered), and makes every in-range citation navigable (v1.19.26):
        /// a quote that matches the page's whitespace-normalized text is
        /// kept verbatim; one that drifted in punctuation or case is
        /// repaired to the page substring it names; one the page cannot
        /// confirm still keeps its circle alive on the cited segment's own
        /// head - real page text either way, because a dead footnote tells
        /// the reader nothing. Only invented or out-of-range ids are
        /// dropped, and inline markers the 'sources' list never backed are
        /// grounded in the segments they named. The 0-based numbering some
        /// models drift into is detected and shifted exactly like the PDF
        /// path does.
        /// </summary>
        public static void ResolveWebSources(List<AiSource>? sources, string? answer, IReadOnlyList<string> segments, string? pageText)
        {
            if (sources is null)
                return;
            if (segments.Count == 0)
            {
                sources.Clear();    // nothing to resolve against: no citations
                return;
            }

            string pageNorm = NormalizeForMatch(pageText);

            // The 0-based drift detection needs id numbers: the sources list
            // when it brought any, the answer's own markers otherwise (a
            // plain-prose reply with no 'sources' JSON still deserves its
            // footnotes).
            List<int> ids = sources.Count > 0
                ? sources.Select(s => AiCitations.ParseSourceId(s.SourceId)).ToList()
                : MarkerNumbers(answer);
            int offset = 0;
            if (ids.Count > 0
                && ids.All(v => v >= 0 && v < segments.Count)
                && ids.Any(v => v == 0))
            {
                offset = 1;
            }

            var kept = new List<AiSource>(sources.Count);
            var seenIds = new HashSet<int>();
            foreach (var src in sources)
            {
                int n = AiCitations.ParseSourceId(src.SourceId) + offset;
                if (n < 1 || n > segments.Count)
                    continue;   // invented/unknown id: drop it rather than guess
                if (!seenIds.Add(n))
                    continue;   // repeated id: keep only the first occurrence

                MakeNavigable(src, n, offset, segments, pageNorm, pageText);
                kept.Add(src);
            }

            // Every inline marker the answer carries lands in the evidence
            // too: the model may cite [SOURCE_3] without listing it, or send
            // prose with markers and no sources JSON at all. A marker naming
            // a real segment must never render as a dead circle.
            foreach (int marker in MarkerNumbers(answer))
            {
                int n = marker + offset;
                if (n < 1 || n > segments.Count || !seenIds.Add(n))
                    continue;

                var src = new AiSource { SourceId = AiCitations.FormatId(marker) };
                MakeNavigable(src, n, offset, segments, pageNorm, pageText);
                kept.Add(src);
            }

            sources.Clear();
            sources.AddRange(kept);
        }

        /// <summary>The quoted passage is verified against the page; anything
        /// the exact compare refuses is repaired, and anything the repair
        /// cannot place falls back to the cited segment's own head. Every
        /// path ends in a quote that is real page text, so the browser's
        /// finder always has something to find.</summary>
        private static void MakeNavigable(
            AiSource src, int n, int offset, IReadOnlyList<string> segments,
            string pageNorm, string? pageText)
        {
            string quote = NormalizeForMatch(src.Quote);
            if (quote.Length >= 3 && pageNorm.Contains(quote))
            {
                src.QuoteVerified = true;
                src.Location = AiQuoteLocation.Exact;
            }
            else
            {
                string? repaired = WebQuoteRepair.Repair(src.Quote, pageText);
                if (!string.IsNullOrEmpty(repaired))
                {
                    src.Quote = repaired;
                    src.Location = AiQuoteLocation.Approximate;
                }
                else
                {
                    src.Quote = SegmentHead(segments[n - 1]);
                    src.Location = AiQuoteLocation.Approximate;
                }
                src.QuoteVerified = true;   // the quote IS page text now
            }

            src.SourceId = offset == 1
                ? AiCitations.FormatId(n - offset)
                : AiCitations.FormatId(n);
            src.PageIndex = -1;
            src.PageNumber = 0;
        }

        /// <summary>Every citation number the answer's inline markers carry,
        /// in order of appearance (duplicates included; the caller dedupes).</summary>
        private static List<int> MarkerNumbers(string? answer)
        {
            var numbers = new List<int>();
            if (string.IsNullOrEmpty(answer))
                return numbers;
            foreach (System.Text.RegularExpressions.Match m in AiCitations.InlineRx.Matches(answer))
            {
                int n = AiCitations.MatchToNumber(m);
                if (n >= 0)
                    numbers.Add(n);
            }
            return numbers;
        }

        /// <summary>The cited segment's own head: the fallback quote when the
        /// model's words cannot be matched to the page - a real page substring
        /// the browser can always find, at the passage the citation names.</summary>
        public const int FallbackQuoteChars = 120;

        public static string SegmentHead(string? segment)
        {
            string norm = NormalizeForMatch(segment);
            if (norm.Length <= FallbackQuoteChars)
                return norm;
            int cut = norm.LastIndexOf(' ', FallbackQuoteChars);
            if (cut < FallbackQuoteChars / 2)
                cut = FallbackQuoteChars;   // no word boundary nearby: a hard cut
            return norm[..cut];
        }

        /// <summary>
        /// Parses one ExecuteScriptAsync result from the citation finder.
        /// The engine returns the script's string as a JSON string literal,
        /// so the result may be double-encoded - both shapes are accepted.
        /// Null whenever nothing usable came back: a gone tab, a timeout, or
        /// a payload that will not read.
        /// </summary>
        public static WebHighlightResult? ParseHighlightResult(string? executeScriptResult)
        {
            if (string.IsNullOrWhiteSpace(executeScriptResult))
                return null;

            string? inner = null;
            try
            {
                using var probe = System.Text.Json.JsonDocument.Parse(executeScriptResult);
                if (probe.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    inner = probe.RootElement.GetString();
            }
            catch
            {
                // Not valid JSON in any shape: the payload below fails the
                // same way and the caller gets its null.
            }

            string payload = inner ?? executeScriptResult;
            if (string.IsNullOrWhiteSpace(payload) || payload == "null")
                return null;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return null;
                bool ok = TryGetBool(root, "ok", out var o) && o;
                string reason = TryGetString(root, "reason", out var r) ? r ?? "" : "";
                bool painted = TryGetBool(root, "painted", out var p) && p;
                return new WebHighlightResult { Ok = ok, Reason = reason, Painted = painted };
            }
            catch
            {
                return null;   // anything unreadable is simply no verdict
            }
        }
    }

    /// <summary>
    /// The quote repair pass (v1.19.26): a model's copied quote usually
    /// fails the page's exact text only in punctuation and case - curly
    /// quotes for straight ones, an em-dash for a hyphen, a ligature, an
    /// ellipsis character, a stray soft hyphen. Both sides fold into a
    /// letters-and-digits shape (case-insensitive, punctuation and symbols
    /// dropped, ligatures expanded, whitespace collapsed) while a map
    /// remembers where every folded character came from; a folded match
    /// maps back onto the page as the literal substring it names - text
    /// the browser's finder can always locate. Null when nothing foldable
    /// matches: the caller falls back to the cited segment's head.
    /// </summary>
    internal static class WebQuoteRepair
    {
        public static string? Repair(string? quote, string? pageText)
        {
            if (string.IsNullOrWhiteSpace(quote) || string.IsNullOrWhiteSpace(pageText))
                return null;

            string page = FoldMap(pageText, out List<int> pageMap);
            string needle = FoldMap(quote, out _);
            // Three folded WORD characters minimum: spaces and punctuation do
            // not count - two letters can sit anywhere, and a repair that
            // short would "confirm" anything.
            if (CountWordChars(needle) < 3)
                return null;

            int at = page.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0)
                return null;

            int start = pageMap[at];
            int end = pageMap[at + needle.Length - 1] + 1;
            if (end <= start || end > pageText.Length)
                return null;
            return WebChat.NormalizeForMatch(pageText[start..end]);
        }

        private static int CountWordChars(string s)
        {
            int n = 0;
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) n++;
            return n;
        }

        /// <summary>Letters and digits only, lowercased; ligatures expand;
        /// control/format characters vanish; whitespace collapses to one
        /// space exactly like NormalizeForMatch. map[i] names the original
        /// index the i-th folded character came from (expansions repeat the
        /// source index, so a span's ends map back onto real characters).</summary>
        private static string FoldMap(string? text, out List<int> map)
        {
            var sb = new System.Text.StringBuilder(text?.Length ?? 0);
            map = new List<int>(text?.Length ?? 0);
            for (int i = 0; i < (text?.Length ?? 0); i++)
            {
                char c = text![i];
                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                    {
                        sb.Append(' ');
                        map.Add(i);
                    }
                    continue;
                }
                if (char.IsPunctuation(c) || char.IsSymbol(c))
                    continue;
                switch (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c))
                {
                    case System.Globalization.UnicodeCategory.Control:
                    case System.Globalization.UnicodeCategory.Format:
                        continue;
                }

                foreach (char ex in Expand(c))
                {
                    sb.Append(char.ToLowerInvariant(ex));
                    map.Add(i);
                }
            }

            string s = sb.ToString();
            int trim = s.Length - s.TrimEnd().Length;
            if (trim > 0)
            {
                map.RemoveRange(map.Count - trim, trim);
                s = s.TrimEnd();
            }
            return s;
        }

        private static string Expand(char c) => c switch
        {
            '\uFB00' => "ff",
            '\uFB01' => "fi",
            '\uFB02' => "fl",
            '\uFB03' => "ffi",
            '\uFB04' => "ffl",
            '\uFB05' or '\uFB06' => "st",
            _ => c.ToString(),
        };
    }
}
