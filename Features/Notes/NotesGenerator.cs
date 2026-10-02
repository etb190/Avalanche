// Features/Notes/NotesGenerator.cs - the 50-page chunked recall digest.
//
// The reader names a page span; Notes produces dense, high-retention review
// cards (~200 words per 50-page block) for rapid recall of what was already
// read. The extraction is the SAME pipeline the summarizer uses (the
// MarkdownNormalizer inside PageSummarizer.ExtractRangeAsync, [p. N] anchors,
// de-hyphenated reflowed paragraphs, native #/##/### headings), so the model
// reads the same token-dense text every other AI feature sees.
//
// Slicing: the requested span travels to the model as ONE continuous prompt
// (gpt-oss:120b-cloud's 128k context spans it whole, so an argument that
// starts on page 129 and finishes on 130 never dangles), but the prompt
// demands the answer STRUCTURED as discrete 50-page cards, each headed
// "## Pages <start> - <end>" - NotesRecallLogic.ParseCards splits those
// headings back into per-block cards the sidebar renders and copies.

namespace Avalanche.Features.Notes
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
    using Avalanche.Features.AI;
    using Avalanche.Features.Summary;
    using Avalanche.Services;

    internal static class NotesGenerator
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(8) };

        private const string NotesSystemPrompt = """
            You are a master analytical reader creating high-yield, comprehensive review notes.
            For each 50-page block you are given, generate a dense, comprehensive note of roughly 200 words following this exact structure:

            1. CORE ARC:
               The central premise, thesis, or primary narrative shift across these 50 pages.
            2. CHRONOLOGICAL PROGRESSION:
               The sequence of ideas, events, and evidence, anchored by page milestones:
               - [pp. X-Y] ...
               - [pp. Y-Z] ...
               - [pp. Z-End] ...
            3. MEMORY PEGS & SPECIFICS:
               The 2-3 most distinct specifics that anchor memory: exact names, central analogies, key case studies, formulas, or pivotal counterarguments.

            RULES:
            - Be dense, concrete, and substantive. Do not use generic filler ("the author discusses", "this section covers").
            - State the actual arguments, findings, and events directly.
            - Keep each card to roughly 200 words total - but NEVER write that count down.

            OUTPUT FORMAT (mandatory):
            - One card per block, in the order the blocks are listed.
            - Each card starts with its own heading line, exactly in this form:
              ## Pages <start> - <end>
            - The three sections are bold labels exactly in this form:
              **CORE ARC:**
              **CHRONOLOGICAL PROGRESSION:**
              **MEMORY PEGS & SPECIFICS:**
            - Milestones are bold page-anchored bullets: - **[pp. X-Y]** ...
            - NEVER output word counts or count annotations - no "(37 words)", "(125 words)" or anything similar, nowhere in the card. The headers carry text only; the reader counts nothing.
            - Nothing before the first heading line and nothing after the last card.
            - The source text carries [p. N] page anchors (and possibly legacy [[p. N]] markers and markdown heading marks). They are scaffolding: never quote them and never use them as headings.
            """;

        /// <summary>Generates the review cards for [firstPage..lastPage].
        /// Throws on provider failure / empty text layer; the sidebar maps the
        /// message onto its localized failure status.</summary>
        public static async Task<List<NoteCard>> GenerateAsync(
            AiProviderConfig config,
            string filePath,
            int firstPage,
            int lastPage,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            progress?.Report("extract");
            string markdown = await PageSummarizer.ExtractRangeAsync(filePath, firstPage, lastPage, ct)
                .ConfigureAwait(false);

            // The same absolute floor the summarizer and the probe apply: a
            // range with (almost) no letters at all has nothing to review.
            string body = System.Text.RegularExpressions.Regex.Replace(
                markdown, @"\[{1,2}p\.\s*\d+\]{1,2}", string.Empty);
            if (AiProbeLogic.CountLetters(body) < 250)
            {
                throw new InvalidOperationException(
                    "the selected pages have no usable text layer (a scanned book without OCR?)");
            }

            var blocks = NotesRecallLogic.ChunkBoundaries(firstPage, lastPage);
            var blockList = new StringBuilder();
            foreach ((int first, int last) in blocks)
            {
                blockList.Append(CultureInfo.InvariantCulture, $"- Pages {first}-{last}\n");
            }

            var user = new StringBuilder();
            user.Append("Blocks to cover (one card per block):\n")
                .Append(blockList)
                .Append('\n')
                .Append(CultureInfo.InvariantCulture, $"=== TEXT (pages {firstPage}-{lastPage}) ===\n")
                .Append(markdown);

            progress?.Report("generate");
            var clock = Stopwatch.StartNew();
            // Reasoning models split max_tokens between their think and the
            // answer: a per-card ceiling of ~200 words plus structure leaves
            // ample room, and caps only cost when they are used.
            int maxTokens = Math.Max(config.MaxTokens, 2000 + 1200 * blocks.Count);
            using var request = BuildNotesRequest(config, user.ToString(), maxTokens);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            SurfaceHealthLog.Log(string.Format(
                CultureInfo.InvariantCulture,
                "notes: POST model={0} pages={1}-{2} blocks={3} chars={4} -> {5} ms",
                config.Model,
                firstPage,
                lastPage,
                blocks.Count,
                markdown.Length,
                clock.ElapsedMilliseconds));

            string? content = AiProbeLogic.ExtractReplyContent(json);
            if (string.IsNullOrWhiteSpace(content))
            {
                string? err = AiProbeLogic.ExtractErrorBody(json);
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(err)
                        ? "the model returned no answer (its whole budget may have gone to hidden reasoning)"
                        : err!);
            }

            var cards = NotesRecallLogic.ParseCards(content, firstPage, lastPage);
            if (cards.Count == 0)
            {
                throw new InvalidOperationException("the model returned no parsable notes");
            }

            SurfaceHealthLog.Log(string.Format(
                CultureInfo.InvariantCulture,
                "notes: {0} card(s) parsed in {1} ms",
                cards.Count,
                clock.ElapsedMilliseconds));
            return cards;
        }

        // Same wire shape as the summary's BuildRequest (OpenAI-compatible chat
        // completions against the configured base URL, Bearer key defaulting to
        // the Ollama placeholder) - a deliberate local copy: the notes budget
        // belongs to this feature and the summarizer's plumbing stays untouched.
        private static HttpRequestMessage BuildNotesRequest(
            AiProviderConfig config, string userText, int maxTokens)
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = config.Model,
                ["messages"] = new object[]
                {
                    new { role = "system", content = NotesSystemPrompt },
                    new { role = "user", content = userText }
                },
                ["temperature"] = config.Temperature,
                ["max_tokens"] = maxTokens,
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
}
