// Features/Summary/PageSummarizer.cs — engine behind the floating page-summary window.
//
// Design (agreed in the feature discussion):
//  * NO embeddings, NO retrieval. The user names an exact page range; retrieval would
//    FILTER, and filtering is the enemy of "miss nothing". We extract the pages' text
//    layer deterministically (TextRunService reading-order runs, same source the
//    selection/search machinery uses) and hand all of it to the chat LLM.
//  * NO CHUNKING (v1.19.51): every letter the range holds is extracted and fed
//    to the model in ONE request - no slicing, no segments, no fusion pass, no
//    fallback road. If a host refuses a call that size, the run fails loudly;
//    a half-answer was never what "miss nothing" meant.
//  * Output contract (v1.12.3): extraction renders compact GitHub-Flavored
//    Markdown (MarkdownNormalizer): de-hyphenated reflowed paragraphs, the
//    book's OWN printed section headings as native # / ## / ### lines (still
//    font-geometry detected, running heads filtered), "- " list items and
//    compact [p. N] page anchors. The digest copies each printed heading
//    VERBATIM as a '### ' markdown heading and summarizes under it in dense
//    flowing prose - no invented headings, no page tags, strict word ceiling.
//    Legacy [[p. N]] / [[H]] markers stay recognized everywhere the new
//    surface could meet old text.
//  * Live paint (v1.19.52): the digest streams straight to the card AS the
//    model writes it - no hidden buffer, no post-hoc reshaping, no retried
//    second draft. What the model says is what the reader sees, bullets and
//    all. Two quiet guards remain and neither touches the prose: a refusal
//    detected at stream-end is reported and never cached, and an answer that
//    never started (a reasoning model that burned its whole budget thinking)
//    retries once at double the token budget before failing loudly - nothing
//    has painted yet, so the retry is invisible.
//  * SSE streaming against the same OpenAI-compatible endpoint the chat uses
//    (AiProviderConfig), with a non-SSE fallback: endpoints that ignore stream:true
//    answer with one JSON body and we surface it as a single delta.
//  * This class has its own serialization gate: the summary can run while the user
//    chats (the chat provider's static semaphore is untouched; Ollama cloud may 429
//    the second concurrent request and the chat path already retries those).

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Avalanche.Features.AI;
    using Avalanche.Services;
    using UglyToad.PdfPig;

    internal sealed record SummaryRequest(
        string FilePath, string DocumentId, int FirstPage, int LastPage, int TargetWords,
        string Language, string Genre, bool BypassCache);

    /// <summary>Kind: "progress" (Text = status line), "delta" (Text = markdown chunk),
    /// "done" (Text = full markdown, FromCache = served from cache), "notext", "error" (Text = message).
    /// RawRange rides on "done" (v1.18.0): the pass's own unabridged extraction, cached by
    /// the window as the floating action popup's Explain source.</summary>
    internal sealed record SummaryUpdate(string Kind, string Text = "", bool FromCache = false, string RawRange = "");

    internal static class PageSummarizer
    {
        // nvidia/nemotron-3-ultra-550b-a55b runs a 1M-token context window:
        // entire books fit in ONE pass, and since v1.19.51 the pipeline trusts
        // that with its whole chest - the entire extracted range rides in one
        // request, whatever it weighs. The hosted NIM endpoint once killed big
        // single calls - but that was thinking mode ON and a 16k answer
        // budget burning minutes of serverless GPU time; with the template
        // switched off and the output capped, hundreds of thousands of
        // chars read in about half a minute. A host that still refuses the
        // call gets an honest failure, not a quietly sliced book.

        // Cloud output ceiling: 16384 tokens of room, so think + answer fit
        // together on the always-thinking dials (kimi, glm, deepseek, gemini)
        // - every one of their output ceilings sits well above this line. The
        // cap bounds the worst case instead of starving the answer; local
        // bridges keep their own ceilings, and a cap only costs when used.
        private const int CloudMaxTokens = 16384;

        // Page anchors: the compact [p. N] form the normalizer emits, plus the
        // legacy [[p. N]] form older text (and older prompts' quotes) can still
        // carry. Every marker strip/split/match below accepts both.
        private const string PageMarkerPattern = @"\[{1,2}p\.\s*\d+\]{1,2}";

        private static readonly SemaphoreSlim Gate = new(1, 1);

        // No auto-timeout on the shared client. HttpClient's own deadline
        // disposes the response the moment it fires, and a reader still pulling
        // a streamed body then dies with "Cannot access a disposed object"
        // (System.Net.Http.HttpConnectionResponseContent) instead of a clean
        // timeout - and with ResponseHeadersRead that deadline spans the whole
        // body consumption, so a long think+answer stream was a ticking crash
        // on its own. Every pass therefore enforces its own budget on a token
        // it owns (PassBudget below, wired into the two pass methods).
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        // The pass budget: the longest silence a pass may hold. A buffered call
        // must finish inside it; a streamed call re-arms the clock on every
        // line, so for streams the budget measures silence - eight quiet
        // minutes fail honestly, a stream that keeps talking may take as long
        // as it takes. Keep this number in step with the two sentences below.
        private static readonly TimeSpan PassBudget = TimeSpan.FromMinutes(8);

        // The two sentences the pass clock throws. The card prints failures
        // verbatim (v1.19.53), so these are written as sentences, not codes.
        private const string StreamQuietError =
            "The provider stopped sending data for over 8 minutes.";
        private const string BufferedTimeoutError =
            "The provider did not answer within 8 minutes.";

        // The learned reasoning_effort drop, per endpoint - the same gate the
        // chat provider runs (OpenAiCompatibleProvider.LearnedFieldDrops): a
        // strict server that 400s the optional field retires it for every
        // later request from this surface, so no pass pays the rejection twice.
        private static readonly ConcurrentDictionary<string, byte> EffortDroppedEndpoints = new();

        // Same identity the chat provider keys its learned drops by.
        private static string EffortEndpointKey(AiProviderConfig config) =>
            $"{config.ProviderType}|{config.BaseUrl}|{config.Model}";

        // One shared run-cache for every extraction path (the digest, the recap,
        // the notes, the tester, the buffer): the (path, ticks, page) key in
        // TextRunService makes stale entries impossible, so pages parsed once
        // stay parsed and a second run over the same stretch parses nothing.
        private static readonly TextRunService ExtractionRuns = new();

        // ------------------------------------------------------------------
        // Token audit (the SummaryWindow verification badge)
        // ------------------------------------------------------------------
        // The badge under the digest is only honest if its numbers belong to the
        // run being shown. The Gate serializes summary runs, so run-scoped counters
        // on this class are safe: GenerateAsync resets them when a run starts, the
        // pass functions accumulate, and the window reads them when "done" arrives.

        /// <summary>Prompt tokens the provider reported reading for the current run,
        /// summed over its passes (the digest pass, plus any guard retry).
        /// Known=false when no response carried usage.</summary>
        public static long RunPromptTokens { get; private set; }

        public static bool RunPromptTokensKnown { get; private set; }

        /// <summary>CJK-aware token estimate of the user content actually sent during
        /// the current run (chars/4 for Latin-style prose, ~1/char for CJK).</summary>
        public static long RunTokensEstimated { get; private set; }

        // ------------------------------------------------------------------
        // Extraction
        // ------------------------------------------------------------------

        /// <summary>Extracts the text layer of [firstPage..lastPage] (1-based, inclusive)
        /// as compact GitHub-Flavored Markdown (MarkdownNormalizer) with compact
        /// [p. N] anchors between pages. Returns null-equivalent empty string when the
        /// pages have no usable text layer (scanned book without OCR).</summary>
        public static Task<string> ExtractRangeAsync(string filePath, int firstPage, int lastPage, CancellationToken ct)
        {
            return Task.Run(
                () =>
                {
                    // One file open for the whole range: PdfPig re-parses the
                    // xref/trailer on every Open, so the old per-page Open turned a
                    // 100-page extraction into 100 file opens. A file that cannot be
                    // opened at all falls through to the per-page road, whose cached
                    // nulls keep the scanned-book verdict (the run ends "notext")
                    // instead of an exception.
                    using PdfDocument? doc = TryOpen(filePath);
                    // Pass 1: render each page's geometry as normalized markdown -
                    // reflowed paragraphs, de-hyphenated words, native #/##/### headings
                    // (font-size detected) and "- " bullets - collecting every heading
                    // candidate for the running-head filter.
                    var candidates = new List<(int Page, string Norm)>();
                    var pageTexts = new Dictionary<int, string>();
                    for (int page = firstPage; page <= lastPage; page++)
                    {
                        ct.ThrowIfCancellationRequested();
                        PageTextRuns? runs = ExtractionRuns.GetPage(filePath, page - 1, doc);
                        pageTexts[page] = runs is null
                            ? string.Empty
                            : MarkdownNormalizer.BuildPageMarkdown(runs, page, candidates);
                    }

                    // Pass 2: a heading that repeats across many pages is page furniture
                    // (book/chapter running head, page-number header), not a section heading.
                    var runningHeads = MarkdownNormalizer.DetectRunningHeads(candidates, lastPage - firstPage + 1);
                    var sb = new StringBuilder();
                    for (int page = firstPage; page <= lastPage; page++)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append("\n\n");
                        }

                        string text = pageTexts[page];
                        if (runningHeads.Count > 0 && text.Length > 0)
                        {
                            text = MarkdownNormalizer.StripRunningHeads(text, runningHeads);
                        }

                        sb.Append("[p. ").Append(page).Append("]\n").Append(text.Trim());
                    }

                    return sb.ToString();
                },
                ct);
        }

        /// <summary>PdfPig's one-shot handle for a whole extraction run, or null when
        /// the file cannot be opened at all - the per-page reads then return their
        /// cached nulls and the run ends "notext", exactly as a scanned book does.</summary>
        private static PdfDocument? TryOpen(string filePath)
        {
            try
            {
                return PdfDocument.Open(filePath);
            }
            catch
            {
                return null;
            }
        }

        // Heading detection, tier rendering, de-hyphenation, paragraph reflow, bullet
        // normalization and the running-head filter live in
        // Features/Summary/MarkdownNormalizer.cs (pure, unit-testable).
        // ------------------------------------------------------------------
        // Generation pipeline
        // ------------------------------------------------------------------

        public static async IAsyncEnumerable<SummaryUpdate> GenerateAsync(
            SummaryRequest request,
            AiProviderConfig config,
            Func<string, string> loc,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            RunPromptTokens = 0;
            RunPromptTokensKnown = false;
            RunTokensEstimated = 0;
            try
            {
                await foreach (SummaryUpdate update in GenerateCoreAsync(request, config, loc, ct))
                {
                    yield return update;
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        // Errors and cancellation intentionally propagate to the caller (the window
        // distinguishes user-stop from failure and renders both). Iterators cannot
        // yield inside try/catch, so no catch clause lives here.
        private static async IAsyncEnumerable<SummaryUpdate> GenerateCoreAsync(
            SummaryRequest request,
            AiProviderConfig config,
            Func<string, string> loc,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new SummaryUpdate(
                    "progress", string.Format(loc("Str_SummaryPreparing"), request.FirstPage, request.LastPage));

                string rangeText = await ExtractRangeAsync(request.FilePath, request.FirstPage, request.LastPage, ct)
                    .ConfigureAwait(false);
                // The pass's own UNABRIDGED extraction (captured before low-text pages are
                // omitted): rides on the "done" update so the window can cache it as the
                // floating action popup's Explain source.
                string rawRangeText = rangeText;
                // Marker-aware gate: strip WHOLE [p. N] / [[p. N]] tokens before counting.
                // The old string.Replace left " N]]" behind (never whitespace) and
                // CountLetters counted the 'p' inside every marker, so a long marker-only
                // range (>= 60 pages of a scanned book) slipped through and the model was
                // asked to summarize bare markers - it answered "no page text was provided".
                string bodyText = System.Text.RegularExpressions.Regex.Replace(
                    rangeText, PageMarkerPattern, string.Empty);
                // Black-box evidence: what was actually pulled out of the file, and from
                // which file. Zero letters here means the answer is the document (no text
                // layer / wrong file), not the model.
                SurfaceHealthLog.Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "summary: pages {0}-{1} extracted {2} chars / {3} letters from \"{4}\"; preview: {5}",
                    request.FirstPage,
                    request.LastPage,
                    bodyText.Length,
                    CountLetters(bodyText),
                    request.FilePath,
                    Preview(bodyText)));
                // Low-text pages: front matter, full-page diagrams, charts, blank
                // chapter plates, scanned leaves. A 100-page stretch of a real book
                // easily carries 30+ of them, and the old gates aborted the WHOLE run
                // at >= 30% such pages (or when the average fell under 100 letters per
                // page) - figure-heavy books were simply unsummarizable. They are now
                // logged and OMITTED from the extraction instead, and the run proceeds
                // over every content-bearing page.
                List<int> lowText = LowTextPages(rangeText, request.FirstPage, request.LastPage);
                if (lowText.Count > 0)
                {
                    SurfaceHealthLog.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "summary: omitting {0} of {1} low-text pages (< 100 letters): {2}",
                        lowText.Count,
                        request.LastPage - request.FirstPage + 1,
                        DescribeRanges(lowText)));
                    rangeText = OmitPages(rangeText, request.FirstPage, request.LastPage, lowText);
                    bodyText = System.Text.RegularExpressions.Regex.Replace(
                        rangeText, PageMarkerPattern, string.Empty);
                }

                // The absolute gate now stands alone: with the thin pages gone, every
                // remaining page carries real prose, so the run aborts only when the
                // whole range holds (almost) no letters at all.
                if (CountLetters(bodyText) < 250)
                {
                    yield return new SummaryUpdate("notext");
                    yield break;
                }

                yield return new SummaryUpdate(
                    "progress",
                    string.Format(
                        loc("Str_SummaryExtracted"), bodyText.Length, request.FirstPage, request.LastPage));

                // Hash + cache lookup run off the UI thread: they touch vector_index.db,
                // which the chat's indexer can hold locked for seconds at a time.
                string hash = await Task.Run(() => SummaryCache.HashText(rangeText), ct).ConfigureAwait(false);
                // Language + word ceiling + genre join the cache identity: the same
                // range in French at 750 words is a different digest than English at
                // 1500, and a fiction retelling is a different digest than a
                // nonfiction-classic one.
                string variant = request.Language + ":" + request.TargetWords.ToString(CultureInfo.InvariantCulture) +
                    ":" + request.Genre;
                if (!request.BypassCache)
                {
                    string? cached = await Task.Run(
                        () => SummaryCache.Get(
                            request.DocumentId, request.FirstPage, request.LastPage, config.Model ?? "?", hash, variant),
                        ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(cached))
                    {
                        if (LooksLikeRefusal(cached))
                        {
                            // An older build could cache the model's refusal as the
                            // "digest"; replaying it makes every retry fail the same
                            // way ("its back to saying..."). Self-heal: treat the
                            // poisoned entry as a miss and regenerate.
                            SurfaceHealthLog.Log(
                                "summary: cache hit for pages " + request.FirstPage + "-" +
                                request.LastPage + " is a stored refusal - ignoring and regenerating");
                        }
                        else
                        {
                            SurfaceHealthLog.Log(string.Format(
                                CultureInfo.InvariantCulture,
                                "summary: cache hit for pages {0}-{1} ({2} chars)",
                                request.FirstPage,
                                request.LastPage,
                                cached.Length));
                            yield return new SummaryUpdate("delta", cached);
                            yield return new SummaryUpdate("done", cached, FromCache: true, RawRange: rawRangeText);
                            yield break;
                        }
                    }
                }

                // THE one call, v1.19.52: the whole extracted range rides in a single
                // streamed request, and the answer paints live as the model writes it -
                // no hidden buffer, no prose guard, no second draft. If the host
                // refuses a call this size the run dies right here with the provider's
                // own error; the reader asked for everything the pages hold, and a
                // quietly halved book was never an answer.
                yield return new SummaryUpdate("progress", loc("Str_SummaryWriting"));
                string digestSystem = DigestSystemPrompt(request.TargetWords, request.Language, request.Genre);
                // Reasoning models split max_tokens between their think and the answer,
                // and a 4,500-word ceiling needs real room: floor at 3k + 4 tokens per
                // target word, never below 10k. Caps only cost when they are used - and
                // on a cloud endpoint this floor is capped right back down, since the
                // hosted tier dies on long requests and no digest needs more.
                int digestBudget = Math.Max(config.MaxTokens, Math.Max(10000, 3000 + (4 * request.TargetWords)));
                if (!AiEndpoints.IsLocal(config.BaseUrl)) digestBudget = Math.Min(digestBudget, CloudMaxTokens);
                var live = new StringBuilder();
                await foreach (SummaryUpdate update in StreamDigestPassAsync(
                                   config, digestSystem, rangeText, ct, digestBudget).ConfigureAwait(false))
                {
                    if (update.Kind == "delta")
                    {
                        live.Append(update.Text);
                        yield return update;
                    }
                }

                string finalText = live.ToString();
                if (string.IsNullOrWhiteSpace(finalText))
                {
                    // A reasoning model can burn its whole budget on hidden thinking
                    // and stream nothing. Nothing has painted, so one quiet retry at
                    // double the token budget is invisible to the reader; still nothing
                    // means the run fails loudly right here.
                    SurfaceHealthLog.Log(
                        "summary: digest pass returned no content - retrying once at double the token budget");
                    finalText = await RunBufferedPassAsync(config, digestSystem, rangeText, ct, digestBudget * 2)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(finalText))
                    {
                        throw new InvalidOperationException(
                            "the model produced no answer (its whole budget went to hidden reasoning) - " +
                            "switch to a non-thinking model or raise the token limit");
                    }
                }

                // A refusal streamed as the "digest" must never be cached (it would
                // replay forever) nor presented as a result. With the thin gate
                // upstream this should be rare - usually a provider hiccup or a
                // context-starved model; the bundle names which.
                if (LooksLikeRefusal(finalText))
                {
                    SurfaceHealthLog.Log(
                        "summary: final text is a model refusal (" + finalText.Length +
                        " chars) - not cached");
                    DiagnosticsBundle.Dump("summary refusal");
                    yield return new SummaryUpdate(
                        "error",
                        "the AI replied with a refusal instead of a digest - it was not cached. " +
                        "Try again (Reset first to skip the cache); if it repeats, switch AI " +
                        "models. A diagnostics file was saved to the Desktop.");
                    yield break;
                }

                SurfaceHealthLog.Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "summary: digest ready: {0} chars / {1} words",
                    finalText.Length,
                    CountWords(finalText)));
                await Task.Run(
                    () => SummaryCache.Put(
                        request.DocumentId, request.FirstPage, request.LastPage, config.Model ?? "?",
                        hash, variant, finalText, CountWords(finalText)),
                    ct).ConfigureAwait(false);
                // The digest painted live while the model wrote it - nothing left
                // to reveal. "done" hands the window the exact final text for the
                // card and the cache, plus the unabridged extraction for Explain.
                yield return new SummaryUpdate("done", finalText, RawRange: rawRangeText);
            }

        // Page refs like (p. 47) -> the set of pages the summary actually touched.
        public static HashSet<int> CoveredPages(string markdown, int firstPage, int lastPage)
        {
            var covered = new HashSet<int>();
            if (string.IsNullOrEmpty(markdown))
            {
                return covered;
            }

            var matches = System.Text.RegularExpressions.Regex.Matches(markdown, @"\(p\.\s*(\d+)\)");
            foreach (var match in matches)
            {
                if (int.TryParse(
                        ((System.Text.RegularExpressions.Match)match!).Groups[1].Value,
                        out int page))
                {
                    if (page >= firstPage && page <= lastPage)
                    {
                        covered.Add(page);
                    }
                }
            }

            return covered;
        }

        /// <summary>True when the text reads like the model's "no text provided"
        /// refusal instead of a digest. Deliberately narrow: the phrases mirror the
        /// digest task framing, so a real digest will not match them.</summary>
        private static bool LooksLikeRefusal(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string flat = text.ToLowerInvariant();
            return flat.Contains("no text provided") ||
                   flat.Contains("no text was provided") ||
                   flat.Contains("no page text") ||
                   flat.Contains("no page content") ||
                   flat.Contains("supply the page content") ||
                   flat.Contains("provide the page content") ||
                   flat.Contains("no content was provided") ||
                   flat.Contains("cannot create a reading digest") ||
                   flat.Contains("unable to create a reading digest");
        }

        public static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            int count = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    inWord = true;
                    count++;
                }
            }

            return count;
        }

        private static int CountLetters(string text)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (char.IsLetter(c))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>First 120 characters of the extracted text, flattened for the log line -
        /// distinguishes real prose from empty extraction and from garbled glyph soup.</summary>
        private static string Preview(string text) => Snippet(text, 120);

        /// <summary>Flattened, length-capped text for log lines.</summary>
        private static string Snippet(string text, int length)
        {
            string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length <= length ? flat : flat[..length];
        }

        /// <summary>choices[0].finish_reason of a completion body/chunk, when present.</summary>
        private static string? ExtractFinishReason(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("finish_reason", out var fin) &&
                    fin.ValueKind == JsonValueKind.String)
                {
                    return fin.GetString();
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>The "error" body of a 200 response that is actually a failure.</summary>
        private static string? ExtractErrorText(string json)
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
                        err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        return m.GetString();
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>choices[0].delta.&lt;name&gt; as a string, when present (reasoning fields).</summary>
        private static string? ExtractDeltaField(string json, string name)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    return null;
                }

                var first = choices[0];
                if (first.TryGetProperty("delta", out var delta) &&
                    delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty(name, out var el) &&
                    el.ValueKind == JsonValueKind.String)
                {
                    return el.GetString();
                }
            }
            catch
            {
            }

            return null;
        }

        // ------------------------------------------------------------------
        // Prompts
        // ------------------------------------------------------------------

        // Transplanted verbatim from the user's pdf-summarizer extension
        // (background.js, shared ANTI_META_LANGUAGE block): bans meta-language,
        // LaTeX/dollar signs, and invented abbreviations.
        private static readonly string AntiMeta =
            """
            ABSOLUTE BAN ON META-LANGUAGE — these phrases are FORBIDDEN, never write them:
            - "The text examines...", "The text explores...", "The text discusses...", "The text illustrates...", "The text investigates...", "The text considers...", "The text analyzes...", "The text looks at...", "The text deals with...", "The text covers...", "The text presents...", "The text describes...", "The text outlines...", "The text reviews...", "The text studies...", "The text delves into...", "The text talks about...", "The text addresses...", "The text examines how..."
            - "The author argues...", "The author shows...", "The author portrays...", "The author suggests...", "The author claims...", "The author explains...", "The author makes the case that...", "The author contends..."
            - "This passage covers...", "This section deals with...", "This section explores...", "This page discusses...", "These pages describe...", "This chapter examines...", "This range covers..."
            - "The book examines...", "The book explores...", "The story explores...", "The narrative focuses on...", "The paper argues...", "The study investigates...", "The chapter argues..."

            If the subject of your sentence is "the text", "the author", "the passage", "this section", "the book", "the story", "the narrative", "the paper", "the study", or "the chapter" — STOP and rewrite that sentence so the subject is the actual person, event, finding, idea, number, or definition.

            THIS SUMMARY IS A SUBSTITUTE FOR READING. The reader is using it INSTEAD of the original. Do NOT describe what the text is about — SAY what the text says. State the actual content (definitions, findings, events, arguments, numbers, names, dates) directly as fact, as if you were the expert teaching it from memory.

            BAD (forbidden — describes the text instead of stating content):
            "The text examines the critical period hypothesis, specifically regarding how age affects pronunciation, grammaticality intuitions, and the overall rate of learning."

            GOOD (states the content directly):
            "After puberty, second-language learners almost never achieve native-like pronunciation; their grammaticality intuitions also plateau. Younger learners outperform them on implicit acquisition, but older learners actually show a faster explicit learning rate in the early stages — the critical period narrows the ceiling, not the speed of early gains."

            BAD (forbidden):  "The author discusses the relationship between sleep and memory."
            GOOD:             "Sleep after learning consolidates memories. REM-sleep deprivation specifically impairs procedural memory tasks, while slow-wave-sleep deprivation impairs declarative memory."

            BAD (forbidden):  "The chapter explores Raskolnikov's moral conflict after the murder."
            GOOD:             "After killing the pawnbroker, Raskolnikov hides the stolen items without using them, falls into a fever, and obsessively revisits the crime scene — convinced he has the right to transgress ordinary morality, yet crushed by guilt he cannot name."

            Before finalizing your summary, re-read every sentence. If any sentence DESCRIBES the text instead of STATING the text's content, rewrite it.

            ABSOLUTE BAN ON LATEX / MATH NOTATION — these are FORBIDDEN in your output:
            - Never write "$\to$", "$\rightarrow$", "$\Rightarrow$", "$\leftarrow$", "$\mapsto$", "$\approx$", "$\leq$", "$\geq$", "$\neq$", "$\in$", "$\sum$", "$\int$", "$\frac{}{}", "$\sqrt{}$", or ANY other LaTeX command.
            - NEVER write a dollar sign ($). Dollar signs are FORBIDDEN. If you are about to write a dollar sign, STOP. You have made a mistake.
            - If you need an arrow, write the literal Unicode character "→" (copy this character: →). Or use the word "to".
            - If you need any other math symbol, write the literal Unicode character directly: ≤, ≥, ≠, ×, ÷, ±, ≈, ∑, ∫, etc.
            - For proportions or ratios, write "X to Y" or "X:Y" — never "$\frac{X}{Y}$".
            - For superscripts/subscripts, write them inline (e.g., "m²" not "$m^2$").

            BAD (forbidden):  "research flows from topic $\to$ thesis $\to$ notes $\to$ draft"
            BAD (forbidden):  "research flows from topic $\rightarrow$ thesis $\rightarrow$ notes $\rightarrow$ draft"
            GOOD:              "research flows from topic → thesis → notes → draft"
            ALSO GOOD:         "research flows from topic to thesis to notes to draft"

            SELF-CHECK BEFORE OUTPUT: Scan your entire response. If it contains ANY dollar sign ($), you have FAILED. Remove every dollar sign and replace any LaTeX command with its plain-text equivalent before sending. Your output is rendered as PLAIN TEXT — it is NOT rendered by a LaTeX engine. Any "$\to$" or "$\rightarrow$" in your output will appear as literal garbage to the reader.

            ABSOLUTE BAN ON CREATING ABBREVIATIONS — NEVER abbreviate proper nouns, technical terms, or multi-word concepts unless the abbreviation appears verbatim in the source text.
            - If the source text says "Covenant Code", you MUST write "Covenant Code" — NEVER abbreviate it to "CC" or "(CC)".
            - If the source text says "Critical Period Hypothesis", you MUST write "Critical Period Hypothesis" — NEVER abbreviate it to "CPH".
            - If the source text says "System 1", you MUST write "System 1" — NEVER abbreviate it to "S1".
            - Do NOT introduce abbreviations in parentheses after the first mention (e.g., do NOT write "Covenant Code (CC)"). Write the full term every time.
            - The ONLY exception: if the source text itself uses an abbreviation (e.g., "DNA", "NASA", "MIS 6"), you may use it as the source does.
            """;

        // The extension's nonfiction checklist, transplanted verbatim (prompt_ai_test
        // Part 1): the old generic "cover the core arguments" rule let models quietly
        // drop the author's analogies, debate positions and named case studies - the
        // very tools the argument rides on. It lives in the head of the digest
        // prompt, so the one pass inherits it with everything else.
        private const string DetailChecklist =
            """
            WHAT COUNTS AS "DETAILS" — ALL of these must be covered:
            - Facts: names, dates, numbers, places, definitions — reproduced exactly.
            - Arguments: the author's claims, thesis, and core reasoning.
            - Evidence: the data, studies, examples, and cases the author uses to support claims.
            - Interpretations: how the author reads the evidence — what they argue it means.
            - Comparisons: EVERY comparison the author makes. If the author compares X to Y, your summary must include that comparison.
            - Parallels and analogies: EVERY parallel or analogy the author draws (historical, biological, or cross-cultural analogies). These are often the author's primary explanatory tools — NEVER omit them.
            - Counterarguments: positions the author disagrees with, debates, and disproves.
            - Specific artifacts, sites, and case studies: every named artifact, site, experiment, courtroom trial, or specific case study. If the author names it, it must appear in the summary.

            FORBIDDEN:
            - Dropping the author's argument and keeping only the raw facts.
            - Merging multiple distinct arguments, comparisons, or parallels into one vague sentence.
            - Replacing a specific parallel or case study with a generic statement like "the author draws parallels" or "studies demonstrate".
            """;

        // ------------------------------------------------------------------
        // Genre personas: the digest prompt is tailored to the book's genre
        // (v1.17.0). The nonfiction classic keeps the transplanted extension
        // prompt and grows the epistemic accuracy rule; the five other genres
        // swap the detail checklist for their own mandate - role, core rules,
        // failure modes. The word-ceiling head, the output-language contract,
        // the anti-meta law, the ground rule, the format block and the
        // notes/anchors plumbing are shared by every genre, so the pipeline
        // mechanics stay identical across personas.
        // ------------------------------------------------------------------
        private const string EpistemicAccuracy =
            """
            EPISTEMIC ACCURACY RULE (CRITICAL): faithfully preserve the author's exact degree of certainty - never flatten a hypothesis into a settled fact.
            - Verified facts: state them as facts ("The excavations at Eridu revealed eighteen distinct temple strata.").
            - Hypotheses, speculations, and interpretations: keep their proper qualifiers ("Evidence suggests...", "Archaeologists hypothesize that seasonal flooding caused the abandonment...").
            - Open debates: name the competing positions and why they clash.
            """;

        private const string FictionMandate =
            """
            ROLE: a dense, chronological event-by-event reteller. You are a condenser, not a literary critic.

            This summary is a SUBSTITUTE for reading the book: the reader is using it INSTEAD of reading the book, so the retelling must stand on its own.

            CORE MANDATE:
            - Every plot event, scene, and character development appears in chronological order (when the book jumps in time, restore the true order and say the jump explicitly).
            - Every scene transition, location shift, and time jump stated explicitly.
            - Every character action, decision, secret, and key line of dialogue reproduced (condensed, but recognizable).
            - Every plot turn, revelation, and complication preserved.
            - Track character dynamics: who is with whom, who knows what secret, and who is doing what.

            FORBIDDEN (FAILURE MODES):
            - Discussing "themes", "symbolism", or "literary devices" - tell the STORY, do not review it.
            - Skipping an event because it seemed "minor" or "transitional".
            - Merging distinct scenes into vague generalities ("adventures continue").
            - Meta-language ("The chapter depicts...").
            """;

        private const string PhilosophicalFictionMandate =
            """
            ROLE: a dual-layer synthesis - non-fiction intellectual rigor fused with narrative drama. Both layers are mandatory.

            This summary is a SUBSTITUTE for reading the book: the reader must get the ideas AND the story.

            LAYER 1 - SUBSTANTIVE PHILOSOPHICAL ARGUMENTS (non-fiction rigor):
            - Extract the core philosophical, moral, theological, or political arguments articulated in dialogues, monologues, or narration.
            - Trace the logical steps, premises, and counterarguments debated by the characters or the author.
            - Name the philosophical positions engaged with (nihilism, determinism, rational egoism, utilitarianism, absurdism, faith) and state what the text concludes about them.

            LAYER 2 - NARRATIVE AND PSYCHOLOGICAL EVENTS:
            - Chronological plot events, scene shifts, encounters, and decisions, without skipping scenes.
            - The psychological crises, moral breakdowns, and confessions of the characters, in detail.
            - Show how the events of the plot directly test, validate, or shatter the philosophical theories the characters hold.
            """;

        private const string ResearchMandate =
            """
            ROLE: an empirical methodology reviewer and quantitative data condenser.

            This summary is a SUBSTITUTE for reading the paper: every claim arrives with its numbers, its test, and its magnitude.

            CORE MANDATE - HIERARCHICAL TIERS (the higher tier wins when space runs short):
            - Tier 1 - STATISTICS AND QUANTITATIVE DATA (top priority): every quantitative result (n, %, mean, median, SD, p-values, confidence intervals, effect sizes, regression coefficients, hazard ratios) reported verbatim, with exact units and referents.
            - Tier 2 - INFERENTIAL REASONING AND MODELS: the exact statistical test, model, or logical step used to bridge data to claims (e.g. a two-way ANOVA, a linear regression controlling for age). State the test and its output.
            - Tier 3 - CONCLUSIONS AND MAGNITUDE: attach every finding to the size of its effect ("reduced infection rate by 24%, 95% CI [16%, 32%]" rather than "had a significant effect").
            - Tier 4 - METHODOLOGY: sample demographics, control conditions, intervention protocols, and instruments.
            - Tier 5 - CONTEXT AND THEORY: the background definitions and the gaps in prior literature the work fills.
            - ADAPTABILITY: if the paper is qualitative or theoretical, report its definitions, frameworks, and qualitative evidence substantively - and never invent numbers.
            """;

        private const string SelfHelpMandate =
            """
            ROLE: a practitioner's executive action notes.

            This summary is a SUBSTITUTE for reading the book: the reader must be able to DO what the book teaches.

            CORE MANDATE:
            - Direct imperative principles: state every principle and technique as a direct command ("When facing X, execute Y because Z" - never "The author suggests doing X").
            - Named models and frameworks: when the author names a framework, matrix, or dichotomy ("System 1 vs System 2", "The Eisenhower Matrix", "Fixed vs Growth Mindset"), state its name, its operational rule, and how to execute it.
            - Exercises and reflection prompts verbatim: reproduce journaling prompts, self-audits, and diagnostic steps verbatim so the reader can actually perform them.
            - Anecdote compression: compress every case study or story into ONE sentence stating the operational lesson. Skip fluff and filler anecdotes.
            """;

        private const string LawMandate =
            """
            ROLE: a judicial clerk preparing a legal doctrine brief.

            This summary is a SUBSTITUTE for reading the text: the reader must get the rule, its elements, and its reach.

            CORE MANDATE:
            - Issues and procedural posture: the constitutional, statutory, or common-law question presented.
            - Holding and rule of law: the binding rule the court established or applied.
            - Doctrinal and statutory elements: enumerate the mandatory conjunctive or disjunctive conditions (1, 2, 3), the evidentiary thresholds, and the burdens of proof.
            - Judicial reasoning and canons of interpretation: how the court applied precedent, statutory plain meaning, or constitutional doctrine.
            - Exceptions and safe harbors: narrowing conditions, affirmative defenses, and statutory exemptions.
            - Dissents: the core legal divergence and counter-doctrine argued by dissenting judges.
            - Preserve exact legal terms of art ("strict scrutiny", "mens rea", "proximate cause").
            """;

        private static string NormalizeGenre(string? genre)
        {
            return genre switch
            {
                "fiction" => "fiction",
                "philosophical_fiction" => "philosophical_fiction",
                "research_papers" => "research_papers",
                "self_help" => "self_help",
                "law" => "law",
                _ => "nonfiction_classic"
            };
        }

        // The genre's persona block: substitute-for-reading line + role + core
        // mandate (no trailing blank line - the caller joins with the anti-meta
        // law). The nonfiction classic keeps the transplanted CRITICAL RULES and
        // DetailChecklist and adds the epistemic accuracy rule beside them.
        private static string GenreMandate(string genre, int targetWords)
        {
            switch (NormalizeGenre(genre))
            {
                case "fiction":
                    return FictionMandate;
                case "philosophical_fiction":
                    return PhilosophicalFictionMandate;
                case "research_papers":
                    return ResearchMandate;
                case "self_help":
                    return SelfHelpMandate;
                case "law":
                    return LawMandate;
                default:
                    return
                        "This summary is a SUBSTITUTE for reading these pages. The reader must understand " +
                        "the key facts, findings, the author's arguments, and the evidence supporting them.\n\n" +
                        "CRITICAL RULES:\n" +
                        "1. Cover the core arguments, evidence, definitions, historical facts, and " +
                        "conclusions directly and factually.\n" +
                        "2. State content directly as facts and findings — never describe the text or the author.\n" +
                        "3. Keep the summary focused, dense, and close to " + targetWords +
                        " words — do NOT exceed " + targetWords + " words.\n\n" +
                        DetailChecklist + "\n\n" +
                        EpistemicAccuracy;
            }
        }

        private static string DigestSystemPrompt(int targetWords, string language, string genre)
        {
            // Word-for-word transplant of the extension's nonfiction_classic
            // multi-page prompt, with two Avalanche adaptations: the ground rule
            // (no outside knowledge) and the marker/tag plumbing note.
            // OUTPUT LANGUAGE: the digest is written in the language the reader picked
            // in the window's dropdown. The book's printed headings are the one exception -
            // they are quoted verbatim in whatever language the book printed them in.
            string languageBlock =
                "OUTPUT LANGUAGE: write every sentence of the summary in " + language + ". The " +
                "one exception is the book's own printed section headings: copy those VERBATIM " +
                "in their original language exactly as printed.\n\n";
            string head =
                "Summarize the following text in approximately " + targetWords +
                " words (do NOT exceed " + targetWords + " words).\n\n" +
                languageBlock;
            return head +
                GenreMandate(genre, targetWords) + "\n\n" +
                AntiMeta + "\n\n" +
                "Use ONLY the provided material; never add outside knowledge, opinions, or meta " +
                "commentary about the text or about summarizing.\n\n" +
                "Format: dense flowing prose paragraphs. Connect the sentences with natural " +
                "transitional phrasing (however, moreover, in practice, as a result) so each " +
                "paragraph reads as one continuous argument rather than stacked fragments. " +
                "No bullet lists, no page tags, no markdown " +
                "decorations of any kind - with exactly one exception: the book's own section " +
                "headings may appear as '### ' headings, as described below.\n\n" +
                "Each page's text starts with a [p. N] anchor (legacy [[p. N]]); the anchors " +
                "tell you which page each part came from, but they must NOT appear in your " +
                "output.\n\n" +
                "BOOK HEADINGS: lines starting with '#', '##' or '###' are the book's own " +
                "printed section headings (native markdown). Copy each one VERBATIM as a " +
                "markdown '### ' heading and summarize the text that follows it under that " +
                "heading, in flowing prose paragraphs. Text before the first heading (the " +
                "range may start mid-section) is ordinary intro prose with no heading. If " +
                "the material has no markdown heading lines at all, write plain prose with " +
                "no headings. NEVER invent a heading and NEVER reword one - use exactly " +
                "the printed heading text. No bullet points and no page tags in your " +
                "output. Bullet points in your answer are a total failure.";
        }

        // ------------------------------------------------------------------
        // The floating action popup's two AI passes (v1.18.0)
        // ------------------------------------------------------------------

        // Define: a fast, focused lexical answer for the word the reader clicked.
        // Buffered like the digest (a reasoning model that burns its whole budget
        // on think gets one retry at double the tokens) and pinned to temperature
        // 0 - a definition is a fact lookup, not a composition.

        public static async Task<string> DefineTermAsync(
            AiProviderConfig config, string term, string language, CancellationToken ct)
        {
            const string system = "You are an authoritative, concise dictionary assistant.";
            string user =
                $"The reader is studying a text in {language} and highlighted the term: \"{term}\".\n" +
                "\n" +
                $"Define \"{term}\" concisely in English (1-3 sentences):\n" +
                "1. Part of speech and core literal definition.\n" +
                "2. If the term is non-English, provide its exact English translation first.\n" +
                "3. Any cultural, technical, or contextual nuance in how the term is used.\n" +
                "Do NOT include conversational meta-talk or introductory pleasantries.";
            int budget = Math.Max(config.MaxTokens, 4096);
            string text = await RunBufferedPassAsync(config, system, user, ct, budget, temperature: 0).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = await RunBufferedPassAsync(config, system, user, ct, budget * 2, temperature: 0).ConfigureAwait(false);
            }

            return text?.Trim() ?? string.Empty;
        }

        // Explain: the popup's core. The raw source text of the active page range
        // (the extraction, [p. N] anchors and all) goes into the prompt so the
        // answer grounds itself in the author's own pages - cites them, quotes
        // them, surfaces what high-level summaries omit - instead of paraphrasing
        // the digest or guessing from training knowledge. Two realities shape the
        // handoff: a request body bigger than the model's context is silently cut
        // down by the provider (the model then only ever sees the first page and
        // truthfully reports the passage missing), and the excerpt is quoted from
        // the AI digest, whose typography differs from the extraction's. So the
        // excerpt is normalized into the source's plain characters, and a range
        // too large for one request travels pre-selected: ExplainSourceSelection,
        // the handoff's pure half, keeps the pages whose text actually overlaps
        // the highlighted words and leaves the far pages home - the model always
        // reads the pages the passage lives on, never a truncation that keeps
        // only page one.

        public static async Task<string> ExplainExcerptAsync(
            AiProviderConfig config, string selectedText, string rawRangeText, int firstPage, int lastPage, CancellationToken ct)
        {
            const string system = "You are a scholarly reading companion.";
            string excerpt = ExplainSourceSelection.NormalizeExcerpt(selectedText);
            string sourceText = ExplainSourceSelection.SelectExcerptPages(
                rawRangeText, excerpt, ExplainSourceSelection.MaxExplainSourceChars, out bool trimmed);
            string scope = trimmed
                ? "RAW SOURCE TEXT (the pages most related to the highlighted passage)"
                : "UNABRIDGED RAW SOURCE TEXT";
            string user =
                "The reader is studying a book and highlighted the following passage/term:\n" +
                $"\"{excerpt}\"\n" +
                "\n" +
                $"Below is the {scope} from pages {firstPage} to {lastPage} of the book:\n" +
                "--- BEGIN SOURCE TEXT ---\n" +
                sourceText + "\n" +
                "--- END SOURCE TEXT ---\n" +
                "\n" +
                "TASK:\n" +
                $"Explain \"{excerpt}\" in depth, grounded STRICTLY in the source text above:\n" +
                "1. **Source Context:** Locate where and how this appears in the source pages. Cite specific page numbers [p. N] and quote surrounding context where helpful.\n" +
                "2. **Author's Meaning:** Explain what the author specifically means by this term/passage in the context of their argument, historical evidence, or narrative scene.\n" +
                "3. **Omitted Nuance:** Highlight any specific details, derivations, dialogue, or caveats present in the original pages that are omitted from high-level summaries.\n" +
                "\n" +
                "RULES:\n" +
                "- The highlighted excerpt is quoted from the reader's digest of these pages, so its wording and typography may differ from the source text (different dash and quote characters, spacing, or number formatting such as \"12 700\" versus \"12,700\"). Locate the corresponding passage semantically; never claim the passage is missing merely because the exact characters differ.\n" +
                "- Base your answer directly on the provided raw source text.\n" +
                "- Only if nothing in the source text corresponds at all: say so in one short sentence, then explain the passage from your general knowledge, clearly marked as such.\n" +
                "- Write in clear, dense prose with bold key concepts.";
            int budget = Math.Max(config.MaxTokens, 8192);
            string text = await RunBufferedPassAsync(config, system, user, ct, budget, temperature: 0).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = await RunBufferedPassAsync(config, system, user, ct, budget * 2, temperature: 0).ConfigureAwait(false);
            }

            return text?.Trim() ?? string.Empty;
        }

        // ------------------------------------------------------------------
        // Page hygiene
        // ------------------------------------------------------------------

        /// <summary>Pages in [firstPage..lastPage] whose extracted text is too thin to
        /// summarize (fewer than ~100 letters): front matter, full-page diagrams,
        /// charts, blank chapter plates, scanned leaves. The model would see nothing
        /// (or page furniture only) for them, so they are omitted from the run.</summary>
        private static List<int> LowTextPages(string rangeText, int firstPage, int lastPage)
        {
            var lowText = new List<int>();
            string[] chunks = System.Text.RegularExpressions.Regex.Split(
                rangeText, PageMarkerPattern);
            for (int i = 1; i < chunks.Length; i++)
            {
                int page = firstPage + i - 1;
                if (page > lastPage)
                {
                    break;
                }

                if (CountLetters(chunks[i]) < 100)
                {
                    lowText.Add(page);
                }
            }

            return lowText;
        }

        /// <summary>Rebuilds the marked range text without the given pages: the text is
        /// split strictly on page markers, so every kept page keeps its own whole
        /// [p. N] block and every segment built from the result still starts at a
        /// page boundary.</summary>
        private static string OmitPages(string rangeText, int firstPage, int lastPage, List<int> omit)
        {
            var drop = new HashSet<int>(omit);
            var kept = new StringBuilder();
            foreach (string block in System.Text.RegularExpressions.Regex.Split(
                rangeText, "(?=" + PageMarkerPattern + ")"))
            {
                string trimmed = block.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var marker = System.Text.RegularExpressions.Regex.Match(
                    trimmed, @"^\[{1,2}p\.\s*(\d+)\]{1,2}");
                if (marker.Success && int.TryParse(marker.Groups[1].Value, out int page) &&
                    page >= firstPage && page <= lastPage && drop.Contains(page))
                {
                    continue;
                }

                if (kept.Length > 0)
                {
                    kept.Append("\n\n");
                }

                kept.Append(trimmed);
            }

            return kept.ToString();
        }

        /// <summary>"40-44, 47, 50-53": consecutive page runs compressed for messages.</summary>
        private static string DescribeRanges(List<int> pages)
        {
            var parts = new List<string>();
            int start = -1, prev = -1;
            foreach (int page in pages.OrderBy(p => p))
            {
                if (start >= 0 && page == prev + 1)
                {
                    prev = page;
                    continue;
                }

                if (start >= 0)
                {
                    parts.Add(start == prev ? start.ToString() : start + "-" + prev);
                }

                start = page;
                prev = page;
            }

            if (start >= 0)
            {
                parts.Add(start == prev ? start.ToString() : start + "-" + prev);
            }

            string joined = string.Join(", ", parts);
            return joined.Length <= 200 ? joined : joined[..200];
        }

        // ------------------------------------------------------------------
        // HTTP + SSE
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // Recap condensation (the Recap companion)
        // ------------------------------------------------------------------

        /// <summary>One-shot condensation for the Recap companion: a single short
        /// paragraph (3-4 sentences) over already-extracted page text. The reader
        /// wants the page's own points, not a creative retelling, so the pass runs
        /// at temperature 0 regardless of the provider profile - and as one
        /// buffered request: the reply is hardened through the same reader as the
        /// digest, where a reasoning-only answer (the whole budget spent thinking)
        /// comes back empty and gets one retry at double the room before the
        /// controller shows its honest failure. Thinking notes never paint.</summary>
        internal static async Task<string> CondenseAsync(
            AiProviderConfig config, string system, string user, CancellationToken ct)
        {
            // A 3-4 sentence paragraph is a few hundred tokens, but a reasoning
            // model hides its spend: floor the ceiling like the digest does. Caps
            // only cost when they are used.
            int budget = Math.Max(config.MaxTokens, 4096);
            string text = await RunBufferedPassAsync(
                config, system, user, ct, budget, temperature: 0).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = await RunBufferedPassAsync(
                    config, system, user, ct, budget * 2, temperature: 0).ConfigureAwait(false);
            }

            return text?.Trim() ?? string.Empty;
        }

        // Whether this request carries the configured reasoning_effort: cloud
        // dials only (local bridges have no effort field), never nemotron (its
        // thinking is the template switch below), only when the reader
        // configured one, and never after the endpoint taught us it rejects
        // the field.
        private static bool EffortEligible(AiProviderConfig config) =>
            !AiEndpoints.IsLocal(config.BaseUrl) &&
            !config.Model.Contains("nemotron", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(config.ReasoningEffort) &&
            !EffortDroppedEndpoints.ContainsKey(EffortEndpointKey(config));

        private static HttpRequestMessage BuildRequest(AiProviderConfig config, string system, string user, int maxTokens, bool stream, double? temperature = null)
        {
            // Cloud output bound: think + answer share one budget on the
            // always-thinking dials, so the ceiling is generous (16384) - but
            // it still bounds the worst case no matter how the callers floored
            // or doubled it - the digest retry, the streaming floor and the
            // popup passes all travel through this one door. A local bridge
            // keeps its own ceilings; a cap only costs when it is used.
            if (!AiEndpoints.IsLocal(config.BaseUrl))
            {
                maxTokens = Math.Min(maxTokens, CloudMaxTokens);
            }

            var body = new Dictionary<string, object?>
            {
                ["model"] = config.Model,
                ["messages"] = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user }
                },
                ["temperature"] = temperature ?? config.Temperature,
                ["max_tokens"] = maxTokens,
                ["stream"] = stream
            };

            // Nemotron reasoning: the thinking mode is a chat-template switch,
            // not a request field, and an unconfigured request is exactly what
            // the endpoint punished. The summarizer wants the digest, not the
            // think, so thinking is explicitly disabled - every token and
            // every second goes to the answer (the sidebar chat turns it on;
            // this side of the app never reads the traces).
            if (config.Model.Contains("nemotron", StringComparison.OrdinalIgnoreCase))
            {
                body["chat_template_kwargs"] = new { enable_thinking = false };
            }

            // Reasoning effort rides along exactly as the chat path sends it
            // (OpenAiCompatibleProvider): kimi, glm, deepseek and gemini all
            // think by default, and an unsent effort let the hidden reasoning
            // eat the whole budget before one word of answer existed. Nemotron
            // is excluded - its thinking is the template switch above, and the
            // summarizer never reads the traces.
            if (EffortEligible(config))
            {
                body["reasoning_effort"] = config.ReasoningEffort;
            }

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

        /// <summary>One send shared by the streaming and buffered digest passes:
        /// builds the request, logs the POST line, and on an HTTP 400 whose body
        /// blames reasoning_effort retires the field for this endpoint and
        /// retries once without it - the chat provider's learned-drop gate,
        /// mirrored. Any other non-success status throws with the provider's
        /// own body verbatim, so the card says what the server said instead of
        /// a bare "HTTP 400".</summary>
        private static async Task<HttpResponseMessage> SendGatedAsync(
            AiProviderConfig config, string system, string user, int maxTokens, bool stream,
            double? temperature, CancellationToken ct)
        {
            bool effortSent = EffortEligible(config);
            for (int attempt = 0; ; attempt++)
            {
                using var request = BuildRequest(config, system, user, maxTokens, stream, temperature);
                SurfaceHealthLog.Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "summary: POST model={0} system={1}ch user={2}ch stream={3} maxTokens={4} effort={5}",
                    config.Model,
                    system.Length,
                    user.Length,
                    stream,
                    maxTokens,
                    effortSent ? config.ReasoningEffort : "off"));
                // Ownership, deliberately loud: this response must OUTLIVE the
                // method - the caller is the one who reads the body. A `using`
                // here disposed the response on the very return that handed it
                // over, and every read afterwards died with "Cannot access a
                // disposed object" (HttpConnectionResponseContent): the
                // v1.19.53 regression that killed every successful pass while
                // every HTTP 400 still quoted itself fine.
                HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                string bodyText;
                try
                {
                    bodyText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
                if (attempt == 0 && effortSent && (int)response.StatusCode == 400 &&
                    bodyText.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase))
                {
                    EffortDroppedEndpoints[EffortEndpointKey(config)] = 1;
                    SurfaceHealthLog.Log(
                        "summary: endpoint rejected reasoning_effort - dropping it for this endpoint and retrying once without the field");
                    response.Dispose();
                    continue;
                }

                int status = (int)response.StatusCode;
                response.Dispose();
                if (string.IsNullOrWhiteSpace(bodyText))
                {
                    // Nothing quotable in the body: keep the friendly status wording.
                    throw new HttpRequestException("HTTP " + status, null, (System.Net.HttpStatusCode)status);
                }

                SurfaceHealthLog.Log("summary: HTTP " + status + " - provider body: " + Snippet(bodyText, 240));
                throw new HttpRequestException(bodyText);
            }
        }

        /// <summary>SendGatedAsync with the pass clock translated: a cancellation the
        /// reader did not issue becomes the honest sentence the card can quote,
        /// instead of a bare "Stopped" for a timeout the reader never chose.
        /// The clock token runs the send; the reader's own token decides
        /// whether a cancellation is a user cancel.</summary>
        private static async Task<HttpResponseMessage> SendTimedAsync(
            AiProviderConfig config, string system, string user, int maxTokens, bool stream,
            double? temperature, CancellationToken ct, CancellationToken passCt, string timedOutError)
        {
            try
            {
                return await SendGatedAsync(config, system, user, maxTokens, stream, temperature, passCt)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException(timedOutError);
            }
        }

        /// <summary>Streams one digest pass: "delta" updates as SSE chunks arrive (or one
        /// delta when the endpoint ignored stream:true). The digest's front door - the
        /// whole range rides in this one streamed request and every delta paints as
        /// it lands.</summary>
        private static async IAsyncEnumerable<SummaryUpdate> StreamDigestPassAsync(
            AiProviderConfig config,
            string system,
            string user,
            [EnumeratorCancellation] CancellationToken ct,
            int maxTokens)
        {
            // The pass clock: HttpClient's auto-timeout is gone (see the
            // field), so each pass arms its own. Armed once here and re-armed
            // on every line below, it measures the longest silence, not the
            // whole answer: a stream that keeps writing a long think+answer
            // may take as long as it takes, a connection that goes quiet dies
            // with an honest sentence instead of a disposed-response crash or
            // an eternal hang.
            using var passCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            passCts.CancelAfter(PassBudget);
            CancellationToken passCt = passCts.Token;

            using var response = await SendTimedAsync(config, system, user, maxTokens, stream: true,
                    null, ct, passCt, StreamQuietError).ConfigureAwait(false);
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // Endpoint ignored stream:true and answered with one JSON body.
                string json;
                try
                {
                    json = await response.Content.ReadAsStringAsync(passCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new HttpRequestException(StreamQuietError);
                }

                CollectRunUsage(json);
                CollectRunSent(user);
                string whole = ExtractMessageContent(json) ?? string.Empty;
                if (whole.Length > 0)
                {
                    yield return new SummaryUpdate("delta", whole);
                }
                else
                {
                    SurfaceHealthLog.Log(
                        "summary: non-stream body without content; snippet: " + Snippet(json, 240));
                }

                yield break;
            }

            CollectRunSent(user);
            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(passCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException(StreamQuietError);
            }

            // The reader alone closes the stream it wraps (StreamReader disposes
            // its underlying stream), so the old explicit `using Stream` - a
            // second hand on the same lever - is gone with the auto-timeout.
            using var reader = new StreamReader(stream);
            var reasoningBuf = new StringBuilder();   // reasoning models stream their think first
            int contentChars = 0;
            string? finish = null;
            while (true)
            {
                if (passCt.IsCancellationRequested)
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }

                    // The clock expired: the provider went quiet - say so.
                    throw new HttpRequestException(StreamQuietError);
                }

                string? line;
                try
                {
                    line = await reader.ReadLineAsync(passCt).ConfigureAwait(false);
                    passCts.CancelAfter(PassBudget);   // bytes landed - restart the silence window
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new HttpRequestException(StreamQuietError);
                }

                if (line is null)
                {
                    break;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                string payload = line.Substring(5).Trim();
                if (payload == "[DONE]")
                {
                    break;
                }

                // Bridges that honor include_usage append the usage frame to the
                // final chunk; the Contains pre-filter keeps per-frame parsing to
                // that one frame instead of every delta.
                if (payload.Contains("\"usage\"", StringComparison.Ordinal))
                {
                    CollectRunUsage(payload);
                }

                string? delta = ExtractDeltaContent(payload);
                if (!string.IsNullOrEmpty(delta))
                {
                    contentChars += delta.Length;
                    yield return new SummaryUpdate("delta", delta);
                    continue;
                }

                string? reasoning = ExtractDeltaField(payload, "reasoning_content")
                    ?? ExtractDeltaField(payload, "reasoning");
                if (!string.IsNullOrEmpty(reasoning))
                {
                    reasoningBuf.Append(reasoning);
                }

                string? fin = ExtractFinishReason(payload);
                if (!string.IsNullOrEmpty(fin))
                {
                    finish = fin;
                }
            }

            SurfaceHealthLog.Log(string.Format(
                CultureInfo.InvariantCulture,
                "summary: stream pass done: {0}ch content / {1}ch reasoning (finish={2})",
                contentChars,
                reasoningBuf.Length,
                finish ?? "?"));
            if (contentChars == 0 && reasoningBuf.Length > 0)
            {
                // The model spent its whole budget thinking and never wrote an answer.
                // The old build "helpfully" handed the raw reasoning to the reader as the
                // summary - hidden bullet-shaped analysis on screen. Thinking notes are
                // not a digest: yield nothing and let the caller retry or abort.
                SurfaceHealthLog.Log(
                    "summary: stream pass produced only reasoning (" + reasoningBuf.Length +
                    "ch), no content - treating as empty");
            }
        }

        private static async Task<string> RunBufferedPassAsync(
            AiProviderConfig config, string system, string user, CancellationToken ct, int maxTokens, double? temperature = null)
        {
            // The buffered pass has no lines to re-arm the clock with, so the
            // budget bounds the whole call - the same eight minutes the shared
            // client used to enforce, minus the disposed-response crash.
            using var passCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            passCts.CancelAfter(PassBudget);
            CancellationToken passCt = passCts.Token;

            using var response = await SendTimedAsync(config, system, user, maxTokens, stream: false,
                    temperature, ct, passCt, BufferedTimeoutError).ConfigureAwait(false);

            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync(passCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException(BufferedTimeoutError);
            }
            CollectRunUsage(json);
            CollectRunSent(user);
            string? content = ExtractMessageContent(json);
            SurfaceHealthLog.Log(string.Format(
                CultureInfo.InvariantCulture,
                "summary: buffered pass -> {0} chars (finish={1})",
                content?.Length ?? 0,
                ExtractFinishReason(json) ?? "?"));
            if (!string.IsNullOrEmpty(content))
            {
                return content;
            }

            // 200-with-no-content: a provider error body, or a reasoning model whose
            // token budget died mid-think. Surface the error; otherwise log the raw
            // shape so the next report names the provider's real response format.
            string? err = ExtractErrorText(json);
            if (!string.IsNullOrEmpty(err))
            {
                throw new HttpRequestException(err);
            }

            SurfaceHealthLog.Log("summary: no content in body; snippet: " + Snippet(json, 240));
            return string.Empty;
        }

        /// <summary>Joins usage.prompt_tokens (OpenAI-compatible) or prompt_eval_count
        /// (Ollama native) out of a response body or SSE frame into the run's token
        /// audit. Bodies without usage - some bridges omit it - simply leave the
        /// counters untouched; the badge then keeps the plain word count instead of
        /// inventing numbers.</summary>
        private static void CollectRunUsage(string json)
        {
            long? tokens = ExtractRunPromptTokens(json);
            if (tokens is long t)
            {
                RunPromptTokens += t;
                RunPromptTokensKnown = true;
            }
        }

        /// <summary>Counts one pass's user content into the run's "what was sent"
        /// estimate - the CJK-aware chars/4 approximation the badge compares against.</summary>
        private static void CollectRunSent(string user)
        {
            RunTokensEstimated += AiProbeLogic.EstimateTokens(user);
        }

        private static long? ExtractRunPromptTokens(string json)
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

        private static string? ExtractDeltaContent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    return null;
                }

                var first = choices[0];
                if (!first.TryGetProperty("delta", out var delta) ||
                    delta.ValueKind != JsonValueKind.Object ||
                    !delta.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.String)
                {
                    return null; // reasoning deltas and role frames are ignored on purpose
                }

                return content.GetString();
            }
            catch
            {
                return null;
            }
        }

        private static string? ExtractMessageContent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var message = choices[0];
                    if (message.TryGetProperty("message", out var msg))
                    {
                        if (msg.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.String &&
                            content.GetString() is { Length: > 0 } text)
                        {
                            return text;
                        }

                        // Reasoning-style endpoints keep the thinking in reasoning_content
                        // (DeepSeek) or reasoning (OpenRouter) while visible content stays
                        // empty. Thinking notes are NOT an answer: returning them as one
                        // fed raw bullet-shaped analysis into digests and notes. Return
                        // null instead - the digest run retries once and then aborts with a
                        // switch-models error, which beats showing thinking garbage.
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        public static string FriendlyError(Exception ex)
        {
            if (ex is HttpRequestException http)
            {
                string message = http.Message;
                if (http.StatusCode.HasValue)
                {
                    int status = (int)http.StatusCode.Value;
                    return status switch
                    {
                        401 or 403 => "authentication failed (" + status + ")",
                        404 => "model or endpoint not found (404)",
                        429 => "provider busy (429) - try again shortly",
                        _ => "HTTP " + status
                    };
                }

                // Verbatim bodies land here (no status code on purpose): the
                // reader asked for the provider's own words, not a summary of
                // them - the cap only guards against a pathological dump.
                return message.Length > 2000 ? message[..2000] : message;
            }

            string text = ex.Message;
            return text.Length > 160 ? text[..160] : text;
        }
    }
}
