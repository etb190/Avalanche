using System;
using System.Collections.Generic;
using System.Linq;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Pure text helpers for the AI chat query/history pipeline - no WPF, no
    /// I/O, so they are unit-testable in isolation (they previously lived as
    /// private methods on the WPF-bound view model).
    /// </summary>
    internal static class AiChatText
    {
        /// <summary>
        /// Builds the retrieval query. A previous question is prepended ONLY
        /// for genuine follow-ups: questions with fewer than three content
        /// terms after stopword removal, or ones opening with a
        /// pronoun/connective. The old length-only rule (&lt; 80 chars)
        /// widened almost every short standalone question, bleeding the
        /// previous topic into otherwise complete questions.
        /// </summary>
        public static string BuildRetrievalQuery(string input, string? previousQuestion)
        {
            var trimmedInput = (input ?? "").Trim();
            if (string.IsNullOrEmpty(trimmedInput))
                return trimmedInput;

            var prev = string.IsNullOrWhiteSpace(previousQuestion) ? null : previousQuestion.Trim();
            if (prev is null || prev == trimmedInput || !LooksLikeFollowUp(trimmedInput))
                return trimmedInput;

            // Cap the PREVIOUS question, never the current one: the old
            // combined[..400] slice cut from the start, so a long previous
            // question could erase the new question entirely.
            var cappedPrev = prev.Length > MaxPreviousChars ? prev[..MaxPreviousChars] : prev;
            var combined = cappedPrev + " " + trimmedInput;
            while (combined.Length > MaxCombinedChars && cappedPrev.Length > 0)
            {
                cappedPrev = cappedPrev[..(cappedPrev.Length - 32)];
                if (cappedPrev.Length > 0) combined = cappedPrev + " " + trimmedInput;
                else combined = trimmedInput;
            }
            return combined;
        }

        private const int MaxPreviousChars = 200;
        private const int MaxCombinedChars = 400;

        /// <summary>
        /// True when the question leans on the previous turn for its subject:
        /// it opens with a pronoun/connective, or it carries fewer than three
        /// content (non-stopword) terms. Stopwords come from the same list the
        /// lexical query builder uses, so "What is the Prophecy of Neferti?"
        /// (three content terms) stays standalone while "why does he think
        /// that?" is widened.
        /// </summary>
        public static bool LooksLikeFollowUp(string input)
        {
            var trimmed = (input ?? "").Trim();
            if (trimmed.Length == 0) return false;

            var first = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                               .FirstOrDefault();
            if (first is not null)
            {
                var firstWord = new string(first.Where(char.IsLetter).ToArray()).ToLowerInvariant();
                if (firstWord.Length > 0 && ConnectiveOpeners.Contains(firstWord))
                    return true;
            }

            return FtsQueryBuilder.CountContentTerms(trimmed) < 3;
        }

        private static readonly HashSet<string> ConnectiveOpeners = new(StringComparer.Ordinal)
        {
            "it", "its", "that", "this", "these", "those", "they", "them", "their",
            "he", "she", "his", "her", "so", "and", "but", "because", "then",
            "also", "more", "else", "explain", "continue"
        };

        /// <summary>
        /// How long a generation took, in the reader's shorthand: plain
        /// seconds under a full minute ("42s"), otherwise minutes and the
        /// leftover seconds ("1m 12s") - a whole minute is just "2m". Shared
        /// by the summarizer's status line and the sidechat's timing line
        /// (v1.19.25).
        /// </summary>
        public static string FormatDuration(TimeSpan elapsed)
        {
            long totalSeconds = (long)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero);
            if (totalSeconds < 0)
                totalSeconds = 0;
            if (totalSeconds < 60)
                return totalSeconds + "s";
            long minutes = totalSeconds / 60;
            long seconds = totalSeconds % 60;
            return seconds == 0 ? minutes + "m" : minutes + "m " + seconds + "s";
        }

        /// <summary>
        /// Assistant history keeps inline [SOURCE_n] markers from the turn
        /// that produced them; the next turn renumbers SOURCE_1..N, so stale
        /// markers can bait wrong citations. Strip them before sending.
        /// </summary>
        public static string StripCitationMarkers(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return AiCitations.InlineRx.Replace(text, "").Trim();
        }
    }
}
