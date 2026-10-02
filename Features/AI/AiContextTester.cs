// Features/AI/AiContextTester.cs — the engine behind the "AI Test" window.
//
// Purpose: mathematical proof that the model actually SAW the pages it was
// asked to read. The summary pipeline trusts Ollama to evaluate every token
// of the extracted text; a silent context truncation (a num_ctx smaller than
// the input, a bridge capping the payload) still returns a fluent, confident
// digest - of half the book. This probe turns that trust into evidence:
//   * it extracts the page range exactly like the summarizer does (the same
//     TextRunService reading-order runs, the same [p. N] anchors - legacy
//     [[p. N]] markers stay recognized),
//   * sends the text to the model with a verbatim-recall instruction (report
//     the first and the last sentence of the text),
//   * audits usage.prompt_tokens (or Ollama's native prompt_eval_count)
//     against a CJK-aware token estimate of what was sent, and
//   * verifies the recalled boundary quotes by window containment: the first
//     quote must live inside the opening 2,000 characters of the flattened
//     text, the last quote inside the closing 2,000 - verbatim, or >= 80%
//     fuzzy against the best-aligned slice of that window. Front matter
//     (covers, copyright pages) and back matter (URLs, watermarks) then
//     cannot split the comparison the way single-sentence regexes did.
// Verdicts: PASS (full context verified), FAIL (tokens read far below what
// was sent - Ollama truncated the document), WARNING (tokens look complete
// but a boundary sentence mismatched). The scoring helpers live in the pure
// static AiProbeLogic so they stay unit-testable without WPF or the PDF
// engine; the SummaryWindow verification badge reuses them too.

namespace Avalanche.Features.AI
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Avalanche.Features.Summary;
    using Avalanche.Services;

    /// <summary>One finished probe run. Ok=false means the probe itself could not
    /// produce evidence (no text layer, or the model refused to answer); every
    /// other field then carries the page range and, when available, the extracted
    /// boundary expectations so the window can still show what it knows.</summary>
    internal sealed record AiTestProbeResult(
        bool Ok,
        string? Error,
        int FirstPage,
        int LastPage,
        string FirstExpected,
        string LastExpected,
        string? FirstRecalled,
        string? LastRecalled,
        int FirstMatch,
        int LastMatch,
        long PromptTokens,
        bool TokensKnown,
        long TokensEstimated,
        int CharsSent,
        double Seconds,
        double TokensPerSecond,
        int SeenPercent,
        bool Truncated,
        string Verdict)
    {
        public static AiTestProbeResult Notext(int firstPage, int lastPage) => new(
            Ok: false, Error: "notext", FirstPage: firstPage, LastPage: lastPage,
            FirstExpected: string.Empty, LastExpected: string.Empty,
            FirstRecalled: null, LastRecalled: null, FirstMatch: 0, LastMatch: 0,
            PromptTokens: 0, TokensKnown: false, TokensEstimated: 0, CharsSent: 0,
            Seconds: 0, TokensPerSecond: 0, SeenPercent: 0, Truncated: false,
            Verdict: "error");

        public static AiTestProbeResult Fault(
            int firstPage, int lastPage, string firstExpected, string lastExpected, string error) => new(
            Ok: false, Error: error, FirstPage: firstPage, LastPage: lastPage,
            FirstExpected: firstExpected, LastExpected: lastExpected,
            FirstRecalled: null, LastRecalled: null, FirstMatch: 0, LastMatch: 0,
            PromptTokens: 0, TokensKnown: false, TokensEstimated: 0, CharsSent: 0,
            Seconds: 0, TokensPerSecond: 0, SeenPercent: 0, Truncated: false,
            Verdict: "error");
    }

    internal static class AiContextTester
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(8) };

        // The probe's contract with the model: verbatim recall, JSON only. The
        // marker note matters - the extracted text is seeded with [p. N] page
        // anchors (legacy [[p. N]]) and markdown heading marks, and a model that
        // quotes the scaffolding as "the first sentence" would fail an otherwise
        // perfect run.
        private const string ProbeSystemPrompt =
            """
            You are a context verification probe. Read the provided text and output a JSON object:
            {
              "first_sentence_seen": "<exact first sentence of the text>",
              "last_sentence_seen": "<exact last sentence of the text>",
              "first_page_marker": "<e.g. [p. 1]>",
              "last_page_marker": "<e.g. [p. 100]>"
            }
            Do not summarize. Return ONLY the JSON object.
            The text carries internal scaffolding: ignore the [p. N] page anchors (and any
            legacy [[p. N]] markers), the markdown # / ## / ### heading marks and any
            [[H]]/[[/H]] wrappers when quoting - they are not part of the sentences.
            The first sentence means: everything from the very start of the text up to and
            including its first sentence-ending punctuation mark. The last sentence means:
            the closing words of the text, from its final sentence start to the very end
            (even if the text ends mid-sentence). Quote both verbatim.
            CRITICAL: Do NOT write extensive internal thinking, analysis, or deliberation.
            Immediately locate the first sentence and last sentence of the text and output ONLY the JSON object.
            """;

        /// <summary>Truncation audit shared with the SummaryWindow badge: a model that
        /// read fewer than 55% of the estimated input tokens never saw the whole range.
        /// The threshold rides well under the estimate's own slack (chars/4 is generous
        /// for Latin prose, so a full read lands near or above 100%).</summary>
        public static (bool Truncated, int SeenPct) Audit(long promptTokens, long estimated) =>
            AiProbeLogic.DecideAudit(promptTokens, estimated);

        /// <summary>The provider-agnostic error wording the summary path already uses
        /// (status codes, timeouts, provider busy) - the test window shows the same
        /// friendly shape for the same failures.</summary>
        public static string FriendlyError(Exception ex) => PageSummarizer.FriendlyError(ex);

        public static async Task<AiTestProbeResult> RunProbeAsync(
            AiProviderConfig config,
            string filePath,
            int firstPage,
            int lastPage,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            progress?.Report("extract");
            string rangeText = await PageSummarizer.ExtractRangeAsync(filePath, firstPage, lastPage, ct)
                .ConfigureAwait(false);

            // The same absolute floor the summarizer uses: a range with (almost) no
            // letters at all has nothing to probe (scanned book without OCR).
            string body = System.Text.RegularExpressions.Regex.Replace(
                rangeText, @"\[{1,2}p\.\s*\d+\]{1,2}", string.Empty);
            if (AiProbeLogic.CountLetters(body) < 250)
            {
                return AiTestProbeResult.Notext(firstPage, lastPage);
            }

            // Boundary expectations, extracted straight from the PDF's text layer -
            // independent of anything the model says. The model is asked to quote
            // from the very start (and the very end) of the TEXT, not of a page, so
            // the expectation is computed the same way: over the whole flattened
            // range with the [[p. N]] markers stripped. A blank cover, an image-only
            // leaf or an un-punctuated title page then cannot empty the expectation
            // - the quote runs through them to the first real punctuation mark.
            // The per-page walk is the belt-and-braces layer: when the whole-range
            // flattening somehow yields nothing, scan forward/backward to the
            // nearest page that still carries prose.
            string flatRange = AiProbeLogic.Flatten(rangeText);
            string firstExpected = AiProbeLogic.FirstSentence(flatRange);
            if (firstExpected.Length == 0)
            {
                for (int p = firstPage; p <= lastPage && firstExpected.Length == 0; p++)
                {
                    firstExpected = AiProbeLogic.FirstSentence(PageBlock(rangeText, p));
                }
            }

            string lastExpected = AiProbeLogic.LastSentence(flatRange);
            if (lastExpected.Length == 0)
            {
                for (int p = lastPage; p >= firstPage && lastExpected.Length == 0; p--)
                {
                    lastExpected = AiProbeLogic.LastSentence(PageBlock(rangeText, p));
                }
            }

            progress?.Report("probe");
            var clock = Stopwatch.StartNew();
            using var request = BuildProbeRequest(config, rangeText);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            clock.Stop();
            SurfaceHealthLog.Log(string.Format(
                CultureInfo.InvariantCulture,
                "ai test: POST model={0} pages={1}-{2} chars={3} -> {4} ms",
                config.Model,
                firstPage,
                lastPage,
                rangeText.Length,
                clock.ElapsedMilliseconds));

            string? content = AiProbeLogic.ExtractReplyContent(json);
            if (string.IsNullOrWhiteSpace(content))
            {
                string? err = AiProbeLogic.ExtractErrorBody(json);
                return AiTestProbeResult.Fault(
                    firstPage, lastPage, firstExpected, lastExpected,
                    string.IsNullOrEmpty(err)
                        ? "the model returned no answer (its whole budget may have gone to hidden reasoning)"
                        : err!);
            }

            var reply = AiProbeLogic.ParseProbeReply(content);
            string? firstRecalled = reply?.FirstSeen;
            string? lastRecalled = reply?.LastSeen;
            SurfaceHealthLog.Log(string.Format(
                CultureInfo.InvariantCulture,
                "ai test: reply parsed: first={0}ch last={1}ch",
                firstRecalled?.Length ?? 0,
                lastRecalled?.Length ?? 0));

            // Window containment, not sentence regexes: the model reports WHERE
            // its memory of the text starts and ends, and front/back matter
            // (covers, copyright pages, URLs, watermarks) makes any exact
            // single-sentence expectation disagree with a faithful recall. The
            // quote only has to live inside the opening / closing window.
            int firstMatch = string.IsNullOrEmpty(firstRecalled)
                ? 0
                : AiProbeLogic.BoundaryMatchPercent(
                    firstRecalled, AiProbeLogic.OpeningWindow(flatRange));
            int lastMatch = string.IsNullOrEmpty(lastRecalled)
                ? 0
                : AiProbeLogic.BoundaryMatchPercent(
                    lastRecalled, AiProbeLogic.ClosingWindow(flatRange));

            long estimated = AiProbeLogic.EstimateTokens(rangeText);
            long? promptTokens = AiProbeLogic.ExtractUsageTokens(json);
            long tokens = promptTokens ?? 0;
            bool tokensKnown = promptTokens.HasValue;
            (bool truncated, int seenPct) = AiProbeLogic.DecideAudit(tokens, estimated);
            string verdict;
            if (tokensKnown && truncated)
            {
                verdict = "fail";
            }
            else if (firstMatch >= 80 && lastMatch >= 80)
            {
                // Tokens unknown (the bridge omitted usage): the boundary recall alone
                // still earns a pass - the model demonstrably saw both ends.
                verdict = "pass";
            }
            else
            {
                verdict = "warn";
            }

            return new AiTestProbeResult(
                Ok: true, Error: null, FirstPage: firstPage, LastPage: lastPage,
                FirstExpected: firstExpected, LastExpected: lastExpected,
                FirstRecalled: firstRecalled, LastRecalled: lastRecalled,
                FirstMatch: firstMatch, LastMatch: lastMatch,
                PromptTokens: tokens, TokensKnown: tokensKnown,
                TokensEstimated: estimated, CharsSent: rangeText.Length,
                Seconds: clock.Elapsed.TotalSeconds,
                TokensPerSecond: completionTokens(json, clock) ,
                SeenPercent: seenPct, Truncated: truncated, Verdict: verdict);

            static double completionTokens(string json, Stopwatch clock)
            {
                long? completion = AiProbeLogic.ExtractCompletionTokens(json);
                return completion is long c && c > 0 && clock.Elapsed.TotalSeconds > 0
                    ? c / clock.Elapsed.TotalSeconds
                    : 0;
            }
        }

        /// <summary>The raw text of one page inside the extracted range: from its
        /// [p. N] anchor (legacy [[p. N]]) to the next anchor (or the end of the
        /// text).</summary>
        private static string PageBlock(string rangeText, int page)
        {
            var marker = System.Text.RegularExpressions.Regex.Match(
                rangeText, @"\[{1,2}p\.\s*" + page.ToString(CultureInfo.InvariantCulture) + @"\]{1,2}");
            if (!marker.Success)
            {
                return string.Empty;
            }

            int start = marker.Index + marker.Length;
            int end = rangeText.Length;
            // Instance Match(input, startat): the static overloads only accept
            // RegexOptions, not a start offset.
            var pageMarker = new System.Text.RegularExpressions.Regex(@"\[{1,2}p\.\s*\d+\]{1,2}");
            var next = pageMarker.Match(rangeText, start);
            if (next.Success)
            {
                end = next.Index;
            }

            return rangeText[start..end];
        }

        // Same wire shape as the summary's BuildRequest (OpenAI-compatible chat
        // completions against the configured base URL, Bearer key defaulting to
        // the Ollama placeholder) - a deliberate local copy: the probe's budget
        // and temperature are its own, and the summarizer's HTTP plumbing stays
        // untouched.
        // The probe's output budget: the configured ceiling, floored at 16384 -
        // a reasoning model's deliberation shares this budget with the answer,
        // and a large document needs the answer to survive the deliberation.
        private static int ProbeTokenBudget(AiProviderConfig config) =>
            Math.Max(config.MaxTokens, 16384);

        private static HttpRequestMessage BuildProbeRequest(AiProviderConfig config, string userText)
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = config.Model,
                ["messages"] = new object[]
                {
                    new { role = "system", content = ProbeSystemPrompt },
                    new { role = "user", content = userText }
                },
                // A recall probe wants determinism: temperature 0, no sampling games
                // between the text and the verdict about the text.
                ["temperature"] = 0,
                // Reasoning models split the output budget between hidden
                // deliberation and the answer: an 80k-token probe died at the old
                // 8192 ceiling with finish_reason=length and an empty content -
                // "no answer". 16384 gives the deliberation room to end, and the
                // second and third spellings reach every bridge (OpenAI's newer
                // name; Ollama's native options.num_predict).
                ["max_tokens"] = ProbeTokenBudget(config),
                ["max_completion_tokens"] = ProbeTokenBudget(config),
                ["options"] = new Dictionary<string, object?> { ["num_predict"] = ProbeTokenBudget(config) },
                ["stream"] = false
            };
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                (config.BaseUrl ?? string.Empty).TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            string key = string.IsNullOrWhiteSpace(config.ApiKey) ? "ollama" : config.ApiKey;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return request;
        }
    }

    /// <summary>Pure scoring logic of the context probe: boundary-sentence extraction,
    /// fuzzy similarity, the CJK-aware token estimate and the truncation decision.
    /// No WPF, no PDF engine, no I/O - everything here is unit-testable on its own.</summary>
    internal static class AiProbeLogic
    {
        // ------------------------------------------------------------------
        // Boundary sentences
        // ------------------------------------------------------------------

        /// <summary>Strips the [p. N] page anchors (and the legacy [[p. N]] form),
        /// the [[H]]/[[/H]] heading wrappers and line-start markdown heading marks,
        /// then collapses every whitespace run to a single space, so both sides of
        /// the comparison quote from the same flattened surface - and a model that
        /// quotes the [p. 2] scaffolding or a '#' heading mark verbatim is not
        /// taxed for characters that were never part of the sentence.</summary>
        public static string Flatten(string pageText)
        {
            string clean = System.Text.RegularExpressions.Regex.Replace(
                pageText ?? string.Empty, @"\[{1,2}p\.\s*\d+\]{1,2}", string.Empty);
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\[\[/?H\]\]", string.Empty);
            // Native markdown heading marks: only at line starts (with the standard
            // 0-3 spaces of indent), so a mid-sentence '#' ("issue #5") survives.
            clean = System.Text.RegularExpressions.Regex.Replace(
                clean, @"(?m)^[ \t]{0,3}#{1,6}[ \t]+", string.Empty);
            return System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
        }

        /// <summary>What the model should quote as the text's first sentence: from the
        /// start up to and including the first sentence-ending mark followed by a
        /// space (or the end of the text), capped at 200 characters. Sentence ends
        /// shorter than six characters are treated as abbreviation fragments ("St.",
        /// "Fig.", "p. 47") and skipped - a genuine first sentence is never that
        /// short, and the fragment rule keeps "St. Augustine wrote..." from cutting
        /// at its very first mark. A title page without any sentence-ending mark
        /// never yields an empty quote: its opening prose (the first 200 characters)
        /// comes back instead.</summary>
        public static string FirstSentence(string pageText)
        {
            string flat = Flatten(pageText);
            if (flat.Length == 0)
            {
                return string.Empty;
            }

            for (int i = 0; i < flat.Length; i++)
            {
                bool terminator = flat[i] is '.' or '!' or '?';
                bool boundary = terminator && (i == flat.Length - 1 || flat[i + 1] == ' ');
                if (!boundary)
                {
                    continue;
                }

                int fragmentLength = i + 1;      // includes the terminator itself
                if (fragmentLength < 6)
                {
                    continue;                     // abbreviation fragment, not a sentence
                }

                return flat[..Math.Min(fragmentLength, 200)].Trim();
            }

            return flat.Length <= 200 ? flat : flat[..200];
        }

        /// <summary>The closing words of the text: everything after the LAST
        /// sentence-ending mark that is followed by a space - "from the final
        /// sentence start to the very end, even mid-sentence", exactly the rule the
        /// probe prompt states. A text with no interior boundary stays whole; a
        /// degenerate tail (stray marks, fragments under six characters) reaches
        /// back one boundary; the result is capped at the final 200 characters.</summary>
        public static string LastSentence(string pageText)
        {
            string flat = Flatten(pageText);
            if (flat.Length == 0)
            {
                return string.Empty;
            }

            int last = -1;
            for (int i = 0; i < flat.Length - 1; i++)
            {
                if (flat[i] is '.' or '!' or '?' && flat[i + 1] == ' ')
                {
                    last = i;
                }
            }

            if (last < 0)
            {
                return flat.Length <= 200 ? flat : flat[^200..];
            }

            string tail = flat[(last + 1)..].Trim();
            if (tail.Length < 6)
            {
                int previous = -1;
                for (int i = 0; i < last; i++)
                {
                    if (flat[i] is '.' or '!' or '?' && flat[i + 1] == ' ')
                    {
                        previous = i;
                    }
                }

                if (previous >= 0)
                {
                    tail = flat[(previous + 1)..].Trim();
                }
            }

            return tail.Length <= 200 ? tail : tail[^200..];
        }

        // ------------------------------------------------------------------
        // Fuzzy comparison
        // ------------------------------------------------------------------

        /// <summary>Similarity of two quotes as a percentage: Levenshtein distance
        /// over the normalized strings (flattened - markup and page markers gone -
        /// then lowercased, everything but letters, digits and underscores removed;
        /// cut points and punctuation stop mattering, only the words the model
        /// actually recalled do). Verbatim containment scores as a full match.</summary>
        public static int SimilarityPercent(string expected, string recalled)
        {
            // Flatten first: the [[p. N]] markers must leave both quotes before the
            // word-character strip, or "[[p. 1]] [[p. 2]]" survives as "p1p2" and
            // taxes an otherwise verbatim recall.
            string a = Normalize(Flatten(expected));
            string b = Normalize(Flatten(recalled));
            if (a.Length == 0 || b.Length == 0)
            {
                return 0;
            }

            int distance = Levenshtein(a, b);
            int similarity = (int)Math.Round(100.0 * (1.0 - (double)distance / Math.Max(a.Length, b.Length)));

            // Containment credit: a model that faithfully quotes MORE than the
            // expected sentence (title + subtitle + the first sentence one page
            // later) or a valid verbatim sub-phrase of it has still recalled the
            // boundary word for word - the shorter normalized string living inside
            // the longer one is a full match. Only quotes with real substance
            // (16+ word characters) earn the credit, so a stray two-letter token
            // cannot ride a long sentence to a pass.
            if (Math.Min(a.Length, b.Length) >= 16 &&
                (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))
            {
                similarity = Math.Max(similarity, 100);
            }

            return similarity;
        }

        // ------------------------------------------------------------------
        // Window containment (the boundary verdict)
        // ------------------------------------------------------------------

        // How much of each end of the flattened text the recall must live in.
        // 2,000 characters reaches past any cover, copyright page or closing
        // URL/watermark block, yet stays small enough that a quote from the
        // book's middle cannot ride a boundary window to a pass.
        public const int BoundaryWindowChars = 2000;

        /// <summary>The opening window: the flattened text's first 2,000 characters
        /// (the whole text when it is shorter). A faithful first-quote recall must
        /// be found in here.</summary>
        public static string OpeningWindow(string flatRange) =>
            Window(flatRange, fromEnd: false);

        /// <summary>The closing window: the flattened text's last 2,000 characters.
        /// A faithful last-quote recall must be found in here.</summary>
        public static string ClosingWindow(string flatRange) =>
            Window(flatRange, fromEnd: true);

        private static string Window(string flatRange, bool fromEnd)
        {
            string text = flatRange ?? string.Empty;
            if (text.Length == 0)
            {
                return string.Empty;
            }

            int take = Math.Min(BoundaryWindowChars, text.Length);
            return fromEnd ? text[^take..] : text[..take];
        }

        /// <summary>Does the recalled quote sit inside the boundary window? Exact
        /// verbatim containment (either direction - a model that quotes past the
        /// window proves the same thing) scores 100; otherwise the best-aligned
        /// same-length slice of the window is fuzzy-compared (a coarse stride
        /// sweep, refined to single positions around the winner), so a recall
        /// with a few divergent characters still clears the 80% pass line the
        /// verdict uses. Quotes shorter than eight normalized characters carry
        /// no evidence worth a verdict and score 0.</summary>
        public static int BoundaryMatchPercent(string recalled, string windowText)
        {
            string window = Normalize(Flatten(windowText));
            string quote = Normalize(Flatten(recalled));
            if (window.Length == 0 || quote.Length == 0 || quote.Length < 8)
            {
                return 0;
            }

            if (window.Contains(quote, StringComparison.Ordinal) ||
                quote.Contains(window, StringComparison.Ordinal))
            {
                return 100;
            }

            if (quote.Length >= window.Length)
            {
                // A recall longer than the window has no slice to align against;
                // the whole-window comparison is the only honest one left.
                return SimilarityPercent(window, quote);
            }

            int best = 0;
            int bestStart = 0;
            int stride = Math.Max(1, (window.Length - quote.Length) / 32);
            for (int start = 0; start + quote.Length <= window.Length; start += stride)
            {
                int sim = SimilarityPercent(quote, window.Substring(start, quote.Length));
                if (sim > best)
                {
                    best = sim;
                    bestStart = start;
                    if (best >= 100)
                    {
                        return best;
                    }
                }
            }

            // Refine around the coarse winner: the stride can land up to half a
            // stride off the true alignment, and that misalignment alone must
            // not cost a faithful recall its pass.
            int from = Math.Max(0, bestStart - stride + 1);
            int to = Math.Min(window.Length - quote.Length, bestStart + stride - 1);
            for (int start = from; start <= to; start++)
            {
                int sim = SimilarityPercent(quote, window.Substring(start, quote.Length));
                if (sim > best)
                {
                    best = sim;
                    if (best >= 100)
                    {
                        break;
                    }
                }
            }

            return best;
        }

        private static string Normalize(string text) =>
            System.Text.RegularExpressions.Regex.Replace(
                (text ?? string.Empty).ToLowerInvariant(), @"[^\w]+", string.Empty);

        private static int Levenshtein(string a, string b)
        {
            if (a == b)
            {
                return 0;
            }

            if (a.Length == 0)
            {
                return b.Length;
            }

            if (b.Length == 0)
            {
                return a.Length;
            }

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int substitute = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                    int insert = previous[j] + 1;
                    int delete = current[j - 1] + 1;
                    current[j] = Math.Min(substitute, Math.Min(insert, delete));
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }

        // ------------------------------------------------------------------
        // Token estimate + truncation decision
        // ------------------------------------------------------------------

        /// <summary>Rough token count of the text the model was sent: Latin-style
        /// prose runs about four characters per token, CJK prose about one token
        /// per character (o200k-family tokenizers). Good enough to catch a bridge
        /// that silently read 4,096 of 62,000 tokens - which is the only thing
        /// this estimate must catch.</summary>
        public static long EstimateTokens(string text)
        {
            long cjk = 0;
            long other = 0;
            foreach (char c in text ?? string.Empty)
            {
                if (IsCjk(c))
                {
                    cjk++;
                }
                else
                {
                    other++;
                }
            }

            return cjk + (other + 3) / 4;
        }

        private static bool IsCjk(char c) =>
            (c >= 0x3040 && c <= 0x30FF) ||    // hiragana + katakana
            (c >= 0x3400 && c <= 0x4DBF) ||    // CJK extension A
            (c >= 0x4E00 && c <= 0x9FFF) ||    // CJK unified
            (c >= 0xAC00 && c <= 0xD7AF) ||    // hangul syllables
            (c >= 0xF900 && c <= 0xFAFF) ||    // CJK compatibility
            (c >= 0xFF66 && c <= 0xFF9D);      // half-width katakana

        /// <summary>The truncation decision shared by the AI test window and the
        /// summary's verification badge: below 55% of the estimated tokens the model
        /// demonstrably never saw the whole input.</summary>
        public static (bool Truncated, int SeenPct) DecideAudit(long promptTokens, long estimated)
        {
            if (estimated <= 0)
            {
                return (false, 0);
            }

            bool truncated = promptTokens * 100 < estimated * 55;
            int seenPct = (int)Math.Min(100L, promptTokens * 100 / estimated);
            return (truncated, seenPct);
        }

        // ------------------------------------------------------------------
        // Response parsing
        // ------------------------------------------------------------------

        /// <summary>choices[0].message.content of a completion body. A spent
        /// output budget can leave the content empty while the answer (or the
        /// deliberation that carries it) sits in the reasoning field - so the
        /// OpenAI-compatible "reasoning_content" (and the bare "reasoning"
        /// spelling) ride as fallbacks before the caller reports "no answer".</summary>
        public static string? ExtractReplyContent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(content.GetString()))
                    {
                        return content.GetString();
                    }

                    // The budget went to hidden reasoning: some bridges write the
                    // answer at the tail of the reasoning field. It parses the same.
                    foreach (string field in new[] { "reasoning_content", "reasoning" })
                    {
                        if (message.TryGetProperty(field, out var reasoning) &&
                            reasoning.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(reasoning.GetString()))
                        {
                            return reasoning.GetString();
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>usage.prompt_tokens (OpenAI-compatible body) or prompt_eval_count
        /// (Ollama native body), when the response carries one.</summary>
        public static long? ExtractUsageTokens(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                    usage.TryGetProperty("prompt_tokens", out var promptTokens) &&
                    promptTokens.TryGetInt64(out long prompt))
                {
                    return prompt;
                }

                if (root.TryGetProperty("prompt_eval_count", out var evalCount) &&
                    evalCount.TryGetInt64(out long count))
                {
                    return count;
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>usage.completion_tokens (or Ollama's eval_count) for the
        /// tokens/sec figure; null when the response omits it.</summary>
        public static long? ExtractCompletionTokens(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                    usage.TryGetProperty("completion_tokens", out var completionTokens) &&
                    completionTokens.TryGetInt64(out long completion))
                {
                    return completion;
                }

                if (root.TryGetProperty("eval_count", out var evalCount) &&
                    evalCount.TryGetInt64(out long count))
                {
                    return count;
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>The "error" message of a 200 body that is actually a failure.</summary>
        public static string? ExtractErrorBody(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.String)
                    {
                        return err.GetString();
                    }

                    if (err.ValueKind == JsonValueKind.Object &&
                        err.TryGetProperty("message", out var message) &&
                        message.ValueKind == JsonValueKind.String)
                    {
                        return message.GetString();
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>Parses the model's JSON object out of its reply: reasoning models
        /// like to wrap the object in ``` fences or prefix prose, so the parser takes
        /// the span from the first '{' to the last '}' and reads the four probe
        /// fields. Null when the reply holds no parseable object.</summary>
        public static (string? FirstSeen, string? LastSeen, string? FirstMarker, string? LastMarker)? ParseProbeReply(
            string content)
        {
            string? json = ExtractJsonBlock(content);
            if (json is null)
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                return (
                    Str(root, "first_sentence_seen"),
                    Str(root, "last_sentence_seen"),
                    Str(root, "first_page_marker"),
                    Str(root, "last_page_marker"));
            }
            catch
            {
                return null;
            }
        }

        private static string? ExtractJsonBlock(string content)
        {
            string text = (content ?? string.Empty).Trim();
            string? json = BalancedJson(text);
            if (json is null && text.Contains("<think>", StringComparison.Ordinal))
            {
                // The bridge routed the answer through the deliberation: search
                // the think payload itself (a spent token budget often leaves it
                // unclosed - everything after the last <think> is the payload).
                int open = text.LastIndexOf("<think>", StringComparison.Ordinal);
                json = BalancedJson(text[(open + "<think>".Length)..]);
            }

            return json;
        }

        // The model's JSON object: the outermost {...} span that actually
        // parses. Reasoning replies wrap the object in ``` fences, prose or
        // deliberation text, so every '{' gets one attempt (against the last
        // '}', the likeliest object end) before the next one - a deliberation
        // that merely mentions braces must not win over the real object.
        private static string? BalancedJson(string text)
        {
            string? fallback = null;
            int start = text.IndexOf('{');
            for (int attempts = 0; start >= 0 && attempts < 8; attempts++)
            {
                int end = text.LastIndexOf('}');
                if (end <= start)
                {
                    break;
                }

                string candidate = text[start..(end + 1)];
                try
                {
                    using var _ = JsonDocument.Parse(candidate);
                    return candidate;
                }
                catch
                {
                    fallback ??= candidate;
                }

                start = text.IndexOf('{', start + 1);
            }

            return fallback;
        }

        private static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>Letters only - the same absolute floor the summarizer applies
        /// before believing a range carries prose.</summary>
        public static int CountLetters(string text)
        {
            int count = 0;
            foreach (char c in text ?? string.Empty)
            {
                if (char.IsLetter(c))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
