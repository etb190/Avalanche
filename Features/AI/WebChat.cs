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
        /// The web sidechat's system prompt: the page itself is the evidence.
        /// Deliberately unlike the PDF prompt there are no SOURCE_n ids and no
        /// citation machinery - a page has no pages to navigate to. The answer
        /// contract stays JSON ("answer") so the provider's tolerant parser
        /// handles both a structured and a plain-prose reply.
        /// </summary>
        public static string BuildSystemPrompt(WebPageSnapshot page)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You are an AI assistant helping a user understand the web page they are reading in the app's browser.");
            sb.AppendLine("The page's text is provided below, extracted from the live tab the user has open.");
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
            sb.AppendLine("Return ONLY a JSON object in this exact shape (no sources field, no citation markers):");
            sb.AppendLine("{\"answer\": \"<your full answer>\"}");
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
            sb.AppendLine("PAGE TEXT:");
            sb.AppendLine("---------- page text begins ----------");
            sb.AppendLine(page.Text);
            sb.AppendLine("---------- page text ends ----------");
            return sb.ToString();
        }
    }
}
