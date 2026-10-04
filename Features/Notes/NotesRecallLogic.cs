// Features/Notes/NotesRecallLogic.cs - pure slicing/parsing logic of the
// Notes sidebar (the 50-page chunked recall digest). No I/O, no WPF, no
// LLM plumbing - the unit-testable core, the AiProbeLogic pattern:
//   * ChunkBoundaries: pages [80..200] slice into [80-129] [130-179]
//     [180-200] so the model returns one dense card per 50-page block.
//   * ParseCards: the model's reply splits on its "## Pages X - Y" heading
//     lines into one NoteCard per block; a reply that ignored the heading
//     contract becomes a single card over the requested span instead of an
//     empty panel.

namespace Avalanche.Features.Notes
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;

    /// <summary>One generated review card: the page block it covers and the
    /// markdown note for it (the model's own heading line stripped).</summary>
    internal sealed record NoteCard(int FirstPage, int LastPage, string Content)
    {
        /// <summary>The card's own Axo save outcome: null until an attempt,
        /// true once the note landed (check face), false when it failed (X
        /// face). Lives on the card, not the button, so the parked snapshot
        /// brings the face back across tab switches.</summary>
        public bool? AxoState { get; set; }
    }

    internal static class NotesRecallLogic
    {
        public const int DefaultChunkSize = 50;

        /// <summary>Slices [firstPage..lastPage] into consecutive blocks of at
        /// most <paramref name="chunkSize"/> pages: 80-200 (chunk 50) yields
        /// (80,129), (130,179), (180,200) - the last block takes the remainder
        /// with a proportional word budget.</summary>
        public static List<(int First, int Last)> ChunkBoundaries(
            int firstPage, int lastPage, int chunkSize = DefaultChunkSize)
        {
            if (chunkSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }

            var list = new List<(int, int)>();
            // Normalize an inverted pair (the UI validates, but stay sane):
            // the blocks always run low page to high page.
            int start = Math.Max(1, Math.Min(firstPage, lastPage));
            int end = Math.Max(start, Math.Max(firstPage, lastPage));
            while (start <= end)
            {
                int stop = Math.Min(end, start + chunkSize - 1);
                list.Add((start, stop));
                start = stop + 1;
            }

            return list;
        }

        // "## Pages 80 - 129" / "### Pages 80-129" / "## Page 80 to 129" /
        // "## PAGES 80 \u2013 129". The heading contract the system prompt
        // dictates; the parser stays liberal about case, padding and dash.
        private static readonly Regex CardHeadingRegex = new(
            @"^#{1,6}\s*Pages?\s*\.?\s*[:\-]?\s*(\d+)\s*(?:[\u2013\u2014-]|to)\s*(\d+)\s*:?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Splits the model's reply into cards. Text before the first
        /// heading (a stray preamble) is dropped; a reply with no heading lines
        /// at all becomes ONE card over the requested span, so a model that
        /// ignored the format still fills the panel instead of failing it.</summary>
        public static List<NoteCard> ParseCards(string reply, int firstPage, int lastPage)
        {
            var cards = new List<NoteCard>();
            if (string.IsNullOrWhiteSpace(reply))
            {
                return cards;
            }

            int curFirst = -1, curLast = -1;
            var content = new List<string>();
            void Flush()
            {
                if (curFirst < 0)
                {
                    return;
                }

                string body = string.Join("\n", content).Trim();
                if (body.Length > 0)
                {
                    cards.Add(new NoteCard(curFirst, curLast, body));
                }

                content.Clear();
            }

            foreach (string raw in reply.Replace("\r\n", "\n").Split('\n'))
            {
                Match m = CardHeadingRegex.Match(raw.Trim());
                if (m.Success &&
                    int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) &&
                    int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int t))
                {
                    Flush();
                    curFirst = Math.Min(f, t);
                    curLast = Math.Max(f, t);
                    continue;
                }

                if (curFirst >= 0)
                {
                    content.Add(raw);
                }
            }

            Flush();

            if (cards.Count == 0)
            {
                string whole = reply.Trim();
                if (whole.Length > 0)
                {
                    cards.Add(new NoteCard(firstPage, lastPage, whole));
                }
            }

            return cards;
        }
    }
}
