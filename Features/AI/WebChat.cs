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
            var segments = SplitPageSegments(page.Text);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You are an AI assistant helping a user understand the web page they are reading in the app's browser.");
            sb.AppendLine("The page's text is provided below as numbered evidence segments, extracted from the live tab the user has open.");
            sb.AppendLine("Answer ONLY using that page text and the conversation so far.");
            sb.AppendLine("If the page text doesn't contain the answer, clearly say so.");
            sb.AppendLine("Distinguish the page's own claims from your own explanation.");
            sb.AppendLine("Never invent page content, quotes, prices, dates or numbers the text does not carry.");
            sb.AppendLine();
            sb.AppendLine("ANSWER STYLE:");
            sb.AppendLine("Write a thorough, well-structured markdown answer that fully covers what the page says about the question.");
            sb.AppendLine("When the page presents several distinct points, answer with short paragraphs or a bulleted list ('- ').");
            sb.AppendLine("Briefly explain terms or context the page assumes when that aids understanding, staying grounded in the page's own text.");
            sb.AppendLine();
            sb.AppendLine("CITATIONS:");
            sb.AppendLine("Inline citations: right after each claim, append the supporting segment's marker in the exact form [SOURCE_n] using plain ASCII square brackets.");
            sb.AppendLine("Example: 'The trial lasted twelve weeks. [SOURCE_2]'.");
            sb.AppendLine("Use [SOURCE_n] only - never full-width brackets like \u3010SOURCE_n\u3011, never (SOURCE_n).");
            sb.AppendLine("Cite only the given SOURCE_n ids; never invent ids or quotes. Quotes must be copied exactly from the cited segment's text.");
            sb.AppendLine("Every source listed in 'sources' must also appear as an inline [SOURCE_n] marker in the answer.");
            sb.AppendLine();
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

            sb.AppendLine("Return ONLY a JSON object in this exact shape:");
            sb.AppendLine("{\"answer\": \"<your full answer with inline [SOURCE_n] markers>\", \"sources\": [{\"sourceId\": \"SOURCE_1\", \"quote\": \"<exact text copied from that segment>\", \"reason\": \"<why it supports the answer>\"}]}");
            sb.AppendLine("The 'quote' must be a short exact excerpt (up to ~300 characters) copied verbatim from the cited segment - it is used to find the passage on the live page.");
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
        /// offered), and drops everything that cannot navigate: invented or
        /// out-of-range ids, duplicates, and quotes the page's own text does
        /// not carry - a quote the snapshot cannot confirm would only send
        /// the browser hunting for words that are not there. The 0-based
        /// numbering some models drift into is detected and shifted exactly
        /// like the PDF path does.
        /// </summary>
        public static void ResolveWebSources(List<AiSource>? sources, IReadOnlyList<string> segments, string? pageText)
        {
            if (sources is null)
                return;
            if (segments.Count == 0)
            {
                sources.Clear();    // nothing to resolve against: no citations
                return;
            }
            if (sources.Count == 0)
                return;

            string pageNorm = NormalizeForMatch(pageText);

            int offset = 0;
            var parsedIds = sources.Select(s => AiCitations.ParseSourceId(s.SourceId)).ToList();
            if (parsedIds.Count > 0
                && parsedIds.All(v => v >= 0 && v < segments.Count)
                && parsedIds.Any(v => v == 0))
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

                string quote = NormalizeForMatch(src.Quote);
                if (quote.Length < 3 || !pageNorm.Contains(quote))
                    continue;   // nothing the browser could find: not a citation

                src.SourceId = offset == 1
                    ? AiCitations.FormatId(n - offset)
                    : AiCitations.FormatId(n);
                src.QuoteVerified = true;
                src.Location = AiQuoteLocation.Exact;
                src.PageIndex = -1;
                src.PageNumber = 0;
                kept.Add(src);
            }

            sources.Clear();
            sources.AddRange(kept);
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
}
