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
//  * Output contract: markdown with "## " topic headings, bullets tagged (p. N) -
//    the window turns those tags into the coverage chips and live page jumps.
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
                    for (int i = 0; i < segments.Count; i++)
                    {
                        var (first, last) = SegmentRange(segments[i]);
                        yield return new SummaryUpdate("progress", string.Format(
                            loc("Str_SummaryPass"), first, last, i + 1, segments.Count + 1));
                        // The old 250 * segments.Count cap starved every note (and left a
                        // reasoning model with nothing after its think). Scale by the
                        // segment's page count instead; a cap only costs when it is used.
                        string note = await RunBufferedPassAsync(
                            config, MiniSystemPrompt(), segments[i], ct,
                            800 + 350 * (last - first + 1));
                        SurfaceHealthLog.Log(string.Format(
                            CultureInfo.InvariantCulture,
                            "summary: segment {0}/{1} (p. {2}-{3}) -> {4} chars",
                            i + 1,
                            segments.Count,
                            first,
                            last,
                            note.Length));
                        notes.Add(note);
                    }

                    // A provider that answers 200 with an error body (or a reasoning model
                    // that puts everything into reasoning_content) yields empty notes; fusing
                    // empties would ask the model to digest nothing, and it would answer
                    // "no page text was provided". Fail loudly instead.
                    if (notes.All(string.IsNullOrWhiteSpace))
                    {
                        yield return new SummaryUpdate(
                            "error", "provider returned no content for every segment pass");
                        yield break;
                    }

                    notes.RemoveAll(string.IsNullOrWhiteSpace);
                    yield return new SummaryUpdate("progress", loc("Str_SummaryWriting"));
                    string fuseInput = string.Join(
                        "\n\n", notes.Select((n, i) => $"--- segment {i + 1} ---\n{n}"));
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

        private static string DigestSystemPrompt(int targetWords, bool fromNotes) =>
            "You create exhaustive reading digests of book pages for a reader who wants to absorb the " +
            "full content without reading the original pages.\n" +
            "Rules:\n" +
            "- Cover EVERY substantive element on the pages: arguments, claims, definitions, facts, " +
            "figures, names, examples, and transitions between ideas. Nothing important may be missing.\n" +
            "- Organize with short '## ' headings by topic; beneath each heading use concise bullet points.\n" +
            "- End every bullet with the page it came from, in the form (p. N).\n" +
            "- Use ONLY the provided material. Never add outside knowledge, opinions, or meta commentary " +
            "about the text or about summarizing.\n" +
            $"- Target length: about {targetWords} words. Prefer completeness over brevity.\n" +
            (fromNotes
                ? "The user message holds working notes from earlier passes; every item already " +
                  "carries its (p. N) page tag. Fuse them into one digest and keep the tags."
                : "Each page's text starts with a [[p. N]] marker.");

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
