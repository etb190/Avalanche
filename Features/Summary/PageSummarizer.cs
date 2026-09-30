// Features/Summary/PageSummarizer.cs — engine behind the floating page-summary window.
//
// Design (agreed in the feature discussion):
//  * NO embeddings, NO retrieval. The user names an exact page range; retrieval would
//    FILTER, and filtering is the enemy of "miss nothing". We extract the pages' text
//    layer deterministically (TextRunService reading-order runs, same source the
//    selection/search machinery uses) and hand all of it to the chat LLM.
//  * <= ~45k chars of text: one streaming pass. Bigger ranges: map-reduce - exhaustive
//    ~250-word notes per ~10-page segment, then a fusion pass that writes the final
//    digest. Progress for every phase is streamed to the window.
//  * Output contract (v1.8.83, transplanted from the user's pdf-summarizer
//    extension "nonfiction classic" prompt): plain dense prose, no headings,
//    no bullet lists, no page tags in the final digest, strict word ceiling.
//    The per-segment notes keep their internal (p. N) tags for the fusion
//    coverage plumbing; the finished digest is plain text.
//  * SSE streaming against the same OpenAI-compatible endpoint the chat uses
//    (AiProviderConfig), with a non-SSE fallback: endpoints that ignore stream:true
//    answer with one JSON body and we surface it as a single delta.
//  * This class has its own serialization gate: the summary can run while the user
//    chats (the chat provider's static semaphore is untouched; Ollama cloud may 429
//    the second concurrent request and the chat path already retries those).

namespace Avalanche.Features.Summary
{
    using System;
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

    internal sealed record SummaryRequest(
        string FilePath, string DocumentId, int FirstPage, int LastPage, int TargetWords, bool BypassCache);

    /// <summary>Kind: "progress" (Text = status line), "delta" (Text = markdown chunk),
    /// "done" (Text = full markdown, FromCache = served from cache), "notext", "error" (Text = message).</summary>
    internal sealed record SummaryUpdate(string Kind, string Text = "", bool FromCache = false);

    internal static class PageSummarizer
    {
        private const int SinglePassCharBudget = 45000;
        private const int SegmentCharBudget = 30000;

        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(8) };

        // ------------------------------------------------------------------
        // Extraction
        // ------------------------------------------------------------------

        /// <summary>Extracts the text layer of [firstPage..lastPage] (1-based, inclusive)
        /// with [[p. N]] markers between pages. Returns null-equivalent empty string when
        /// the pages have no usable text layer (scanned book without OCR).</summary>
        public static Task<string> ExtractRangeAsync(string filePath, int firstPage, int lastPage, CancellationToken ct)
        {
            return Task.Run(
                () =>
                {
                    var runsService = new TextRunService();
                    var sb = new StringBuilder();
                    for (int page = firstPage; page <= lastPage; page++)
                    {
                        ct.ThrowIfCancellationRequested();
                        PageTextRuns? runs = runsService.GetPage(filePath, page - 1);
                        string text = runs is null
                            ? string.Empty
                            : TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _);
                        if (sb.Length > 0)
                        {
                            sb.Append("\n\n");
                        }

                        sb.Append("[[p. ").Append(page).Append("]]\n").Append(text.Trim());
                    }

                    return sb.ToString();
                },
                ct);
        }

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
                // Marker-aware gate: strip WHOLE [[p. N]] tokens before counting. The old
                // string.Replace left " N]]" behind (never whitespace) and CountLetters
                // counted the 'p' inside every marker, so a long marker-only range (>= 60
                // pages of a scanned book) slipped through and the model was asked to
                // summarize bare markers - it answered "no page text was provided".
                string bodyText = System.Text.RegularExpressions.Regex.Replace(
                    rangeText, @"\[\[p\.\s*\d+\]\]", string.Empty);
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
                if (CountLetters(bodyText) < 60)
                {
                    yield return new SummaryUpdate("notext");
                    yield break;
                }

                // Thin extraction: letters exist but there is no prose to digest
                // (headers, page numbers, a broken text layer). The absolute gate
                // above only catches < 60 letters TOTAL, so a 60-page range of
                // ~40-letter pages sailed through, collapsed into ONE segment, and
                // the model answered with the digest-framed refusal "no text
                // provided" (seen in the field). Proportional floor: ~100 letters
                // per page - real prose pages run 1000+, so nothing anyone would
                // want summarized is lost.
                int rangedPages = request.LastPage - request.FirstPage + 1;
                if (CountLetters(bodyText) < 100 * rangedPages)
                {
                    SurfaceHealthLog.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "summary: extraction too thin: {0} letters across {1} pages (< 100/page) - aborting",
                        CountLetters(bodyText),
                        rangedPages));
                    yield return new SummaryUpdate(
                        "error",
                        string.Format(
                            loc("Str_SummaryNoTextRange"),
                            DescribeRanges(Enumerable.Range(request.FirstPage, rangedPages).ToList())));
                    yield break;
                }

                // Pages that yielded (almost) no text: a digest built anyway would
                // silently skip them, and the reader would trust coverage the model
                // never saw. Log the holes; at >= 30% textless pages the run cannot
                // miss nothing, so it fails with the exact ranges instead of
                // shipping a digest that quietly ignores part of the range.
                List<int> textless = TextlessPages(rangeText, request.FirstPage, request.LastPage);
                if (textless.Count > 0)
                {
                    SurfaceHealthLog.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "summary: {0} of {1} pages have no text layer: {2}",
                        textless.Count,
                        request.LastPage - request.FirstPage + 1,
                        DescribeRanges(textless)));
                    if (textless.Count * 10 >= (request.LastPage - request.FirstPage + 1) * 3)
                    {
                        yield return new SummaryUpdate(
                            "error",
                            string.Format(loc("Str_SummaryNoTextRange"), DescribeRanges(textless)));
                        yield break;
                    }
                }

                yield return new SummaryUpdate(
                    "progress",
                    string.Format(
                        loc("Str_SummaryExtracted"), bodyText.Length, request.FirstPage, request.LastPage));

                // Hash + cache lookup run off the UI thread: they touch vector_index.db,
                // which the chat's indexer can hold locked for seconds at a time.
                string hash = await Task.Run(() => SummaryCache.HashText(rangeText), ct).ConfigureAwait(false);
                if (!request.BypassCache)
                {
                    string? cached = await Task.Run(
                        () => SummaryCache.Get(
                            request.DocumentId, request.FirstPage, request.LastPage, config.Model ?? "?", hash),
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
                            yield return new SummaryUpdate("done", cached, FromCache: true);
                            yield break;
                        }
                    }
                }

                List<string> segments = SegmentPages(rangeText);
                string finalText;
                if (segments.Count == 1)
                {
                    var collected = new StringBuilder();
                    await foreach (SummaryUpdate update in StreamDigestPassAsync(
                        config, DigestSystemPrompt(request.TargetWords, fromNotes: false), rangeText, ct))
                    {
                        if (update.Kind == "delta")
                        {
                            collected.Append(update.Text);
                        }

                        yield return update;
                    }

                    finalText = collected.ToString();
                }
                else
                {
                    var notes = new List<string>();
                    var missing = new List<int>();
                    for (int i = 0; i < segments.Count; i++)
                    {
                        var (first, last) = SegmentRange(segments[i]);
                        yield return new SummaryUpdate("progress", string.Format(
                            loc("Str_SummaryPass"), first, last, i + 1, segments.Count + 1));
                        // The old 250 * segments.Count cap starved every note (and left a
                        // reasoning model with nothing after its think). Scale by the
                        // segment's page count instead; a cap only costs when it is used.
                        int noteBudget = 800 + 350 * (last - first + 1);
                        string note = string.Empty;
                        try
                        {
                            note = await RunBufferedPassAsync(
                                config, MiniSystemPrompt(), segments[i], ct, noteBudget);
                            if (string.IsNullOrWhiteSpace(note))
                            {
                                // Buffered calls are the flakiest path (rate limits,
                                // think-only answers, silent 200s). One quiet retry of
                                // each kind before the segment is declared lost.
                                await Task.Delay(1200, ct).ConfigureAwait(false);
                                note = await RunBufferedPassAsync(
                                    config, MiniSystemPrompt(), segments[i], ct, noteBudget);
                            }

                            if (string.IsNullOrWhiteSpace(note))
                            {
                                var streamed = new StringBuilder();
                                await foreach (SummaryUpdate update in StreamDigestPassAsync(
                                    config, MiniSystemPrompt(), segments[i], ct))
                                {
                                    if (update.Kind == "delta")
                                    {
                                        streamed.Append(update.Text);
                                    }
                                }

                                note = streamed.ToString();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // A hard provider error on one segment must not kill the
                            // whole run; the segment lands in "missing" and the
                            // coverage guard below does the talking.
                            SurfaceHealthLog.Log(
                                "summary: segment " + (i + 1) + "/" + segments.Count +
                                " failed: " + FriendlyError(ex));
                        }

                        SurfaceHealthLog.Log(string.Format(
                            CultureInfo.InvariantCulture,
                            "summary: segment {0}/{1} (p. {2}-{3}) -> {4} chars",
                            i + 1,
                            segments.Count,
                            first,
                            last,
                            note.Length));
                        if (string.IsNullOrWhiteSpace(note))
                        {
                            missing.AddRange(Enumerable.Range(first, last - first + 1));
                            continue;
                        }

                        notes.Add(note);
                    }

                    // A provider that answers 200 with an error body (or a reasoning model
                    // that puts everything into reasoning_content) yields empty notes; fusing
                    // empties would ask the model to digest nothing, and it would answer
                    // "no page text was provided". Fail loudly instead.
                    if (notes.Count == 0)
                    {
                        yield return new SummaryUpdate(
                            "error", "provider returned no content for every segment pass");
                        yield break;
                    }

                    // Partial coverage is a failure, not a summary: fusing the survivors
                    // ships a digest that quietly skips every dead segment (the "only
                    // pages 91-100 came back" report). Name the holes instead of
                    // pretending the range is covered.
                    if (missing.Count > 0)
                    {
                        yield return new SummaryUpdate(
                            "error",
                            "segment passes returned no content for pages " + DescribeRanges(missing) +
                            " - the digest was aborted instead of silently covering part of " +
                            "the range. Try again; if it repeats, switch AI models.");
                        yield break;
                    }

                    yield return new SummaryUpdate("progress", loc("Str_SummaryWriting"));
                    string fuseInput = string.Join(
                        "\n\n", notes.Select((n, i) => $"--- segment {i + 1} ---\n{n}"));
                    SurfaceHealthLog.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "summary: fusion input: {0}/{1} notes, {2} chars",
                        notes.Count,
                        segments.Count,
                        fuseInput.Length));
                    var fused = new StringBuilder();
                    await foreach (SummaryUpdate update in StreamDigestPassAsync(
                        config, DigestSystemPrompt(request.TargetWords, fromNotes: true), fuseInput, ct))
                    {
                        if (update.Kind == "delta")
                        {
                            fused.Append(update.Text);
                        }

                        yield return update;
                    }

                    finalText = fused.ToString();
                }

                if (string.IsNullOrWhiteSpace(finalText))
                {
                    yield return new SummaryUpdate("error", "empty response");
                    yield break;
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
                        hash, finalText, CountWords(finalText)),
                    ct).ConfigureAwait(false);
                yield return new SummaryUpdate("done", finalText);
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

        private static string DigestSystemPrompt(int targetWords, bool fromNotes)
        {
            // Word-for-word transplant of the extension's nonfiction_classic
            // multi-page prompt, with two Avalanche adaptations: the ground rule
            // (no outside knowledge) and the marker/tag plumbing note.
            string head =
                "Summarize the following text in approximately " + targetWords +
                " words (do NOT exceed " + targetWords + " words).\n\n" +
                "This summary is a SUBSTITUTE for reading these pages. The reader must understand " +
                "the key facts, findings, the author's arguments, and the evidence supporting them.\n\n" +
                "CRITICAL RULES:\n" +
                "1. Cover the core arguments, evidence, definitions, historical facts, and " +
                "conclusions directly and factually.\n" +
                "2. State content directly as facts and findings — never describe the text or the author.\n" +
                "3. Keep the summary focused, dense, and close to " + targetWords +
                " words — do NOT exceed " + targetWords + " words.\n\n" +
                AntiMeta + "\n\n" +
                "Use ONLY the provided material; never add outside knowledge, opinions, or meta " +
                "commentary about the text or about summarizing.\n\n" +
                "Plain text only: no headings, no bullet lists, no markdown formatting, and no " +
                "page tags in the output.\n\n";
            return head + (fromNotes
                ? "The user message holds working notes from earlier passes; every item already " +
                  "carries its (p. N) page tag. Fuse them into ONE summary of your own: merge " +
                  "duplicates, drop filler, and cover the full span the notes cover, from their " +
                  "first page tag to their last. Never copy the notes verbatim and never return " +
                  "one segment's notes unchanged."
                : "Each page's text starts with a [[p. N]] marker; the markers tell you which " +
                  "page each part came from, but they must NOT appear in your output.");
        }

        private static string MiniSystemPrompt() =>
            "You produce exhaustive working notes from book page segments that will later be fused into " +
            "one digest.\n" +
            "Rules: list every argument, definition, fact, figure, name and example in order, one bullet " +
            "per item, ending each bullet with (p. N) using the [[p. N]] markers. No headings, no " +
            "commentary, no outside knowledge. Do not omit anything substantive.";

        // ------------------------------------------------------------------
        // Segmentation
        // ------------------------------------------------------------------

        private static List<string> SegmentPages(string rangeText)
        {
            var segments = new List<string>();
            var current = new StringBuilder();
            foreach (string page in rangeText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.Length > 0 && current.Length + page.Length > SegmentCharBudget)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append("\n\n");
                }

                current.Append(page);
            }

            if (current.Length > 0)
            {
                segments.Add(current.ToString());
            }

            return segments;
        }

        /// <summary>Pages in [firstPage..lastPage] whose extracted text is essentially
        /// empty (fewer than ~20 letters): the model would see nothing for them.</summary>
        private static List<int> TextlessPages(string rangeText, int firstPage, int lastPage)
        {
            var textless = new List<int>();
            string[] chunks = System.Text.RegularExpressions.Regex.Split(
                rangeText, @"\[\[p\.\s*\d+\]\]");
            for (int i = 1; i < chunks.Length; i++)
            {
                int page = firstPage + i - 1;
                if (page > lastPage)
                {
                    break;
                }

                if (CountLetters(chunks[i]) < 20)
                {
                    textless.Add(page);
                }
            }

            return textless;
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

        private static (int First, int Last) SegmentRange(string segment)
        {
            int first = -1, last = -1;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(segment, @"\[\[p\.\s*(\d+)\]\]"))
            {
                int page = int.Parse(m.Groups[1].Value);
                if (first < 0)
                {
                    first = page;
                }

                last = page;
            }

            if (first < 0)
            {
                first = 1;
                last = 1;
            }

            return (first, last);
        }

        // ------------------------------------------------------------------
        // HTTP + SSE
        // ------------------------------------------------------------------

        private static HttpRequestMessage BuildRequest(AiProviderConfig config, string system, string user, int maxTokens, bool stream)
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = config.Model,
                ["messages"] = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user }
                },
                ["temperature"] = config.Temperature,
                ["max_tokens"] = maxTokens,
                ["stream"] = stream
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

        /// <summary>Streams one digest pass: "delta" updates as SSE chunks arrive (or one
        /// delta when the endpoint ignored stream:true).</summary>
        private static async IAsyncEnumerable<SummaryUpdate> StreamDigestPassAsync(
            AiProviderConfig config,
            string system,
            string user,
            [EnumeratorCancellation] CancellationToken ct)
        {
            // Reasoning models split max_tokens between their think and the answer;
            // 6000 left ~200-word digests. Caps only cost when they are actually used.
            using var request = BuildRequest(config, system, user, Math.Max(config.MaxTokens, 10000), stream: true);
            SurfaceHealthLog.Log(string.Format(
                CultureInfo.InvariantCulture,
                "summary: POST model={0} system={1}ch user={2}ch stream=true",
                config.Model,
                system.Length,
                user.Length));
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // Endpoint ignored stream:true and answered with one JSON body.
                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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

            using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var reasoningBuf = new StringBuilder();   // reasoning models stream their think first
            int contentChars = 0;
            string? finish = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
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
                // The model spent its whole budget thinking and never wrote an answer -
                // hand the reader the reasoning rather than an empty digest.
                yield return new SummaryUpdate("delta", Snippet(reasoningBuf.ToString(), 8000));
            }
        }

        private static async Task<string> RunBufferedPassAsync(
            AiProviderConfig config, string system, string user, CancellationToken ct, int maxTokens)
        {
            using var request = BuildRequest(config, system, user, maxTokens, stream: false);
            SurfaceHealthLog.Log(string.Format(
                CultureInfo.InvariantCulture,
                "summary: POST model={0} system={1}ch user={2}ch stream=false maxTokens={3}",
                config.Model,
                system.Length,
                user.Length,
                maxTokens));
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
                throw new HttpRequestException(err.Length > 200 ? err[..200] : err);
            }

            SurfaceHealthLog.Log("summary: no content in body; snippet: " + Snippet(json, 240));
            return string.Empty;
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

                        // Reasoning-style endpoints keep the answer in reasoning_content
                        // (DeepSeek) or reasoning (OpenRouter) while visible content stays
                        // empty - returning it beats nothing.
                        if (msg.TryGetProperty("reasoning_content", out var reasoning) &&
                            reasoning.ValueKind == JsonValueKind.String &&
                            reasoning.GetString() is { Length: > 0 } rtext)
                        {
                            return rtext;
                        }

                        if (msg.TryGetProperty("reasoning", out var reasoningAlt) &&
                            reasoningAlt.ValueKind == JsonValueKind.String &&
                            reasoningAlt.GetString() is { Length: > 0 } rtextAlt)
                        {
                            return rtextAlt;
                        }
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

                return message.Length > 160 ? message[..160] : message;
            }

            string text = ex.Message;
            return text.Length > 160 ? text[..160] : text;
        }
    }
}
