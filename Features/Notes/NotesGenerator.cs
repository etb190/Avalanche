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
// (nvidia/nemotron-3-ultra-550b-a55b's 1M context spans it whole, so an argument
// that starts on page 129 and finishes on 130 never dangles), but the prompt
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

        // Cloud free-tier guard, shared with the summarizer: the hosted
        // endpoint answers an 8192-token request and kills a bigger one,
        // and ~200-word cards never needed more room than that anyway.
        private const int CloudMaxTokens = 8192;

        // v1.19.89: the contract rides the prompt workshop - the library
        // holds the built-in wording, the store overrides it.
        private static string NotesSystemPrompt => Features.AI.AiPromptLibrary.Notes();

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
            // Cloud free-tier guard: same ceiling as the summarizer's
            // requests - the hosted endpoint kills long-running ones, and
            // the doubled budgets a local bridge may keep are exactly what
            // the free tier answers with HTTP 500.
            if (!AiEndpoints.IsLocal(config.BaseUrl))
            {
                maxTokens = Math.Min(maxTokens, CloudMaxTokens);
            }

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

            // Nemotron reasoning: the notes want cards, not think traces -
            // thinking is explicitly disabled for the same reason the
            // summarizer disables it (the sidebar chat turns it on instead).
            if (config.Model.Contains("nemotron", StringComparison.OrdinalIgnoreCase))
            {
                body["chat_template_kwargs"] = new { enable_thinking = false };
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
    }
}
