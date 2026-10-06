// Features/Summary/RecapController.cs — the Recap companion's brain.
//
// Recap is the memory bridge between pages: the stretch the reader names in
// the summary navigator condenses into a single 3-4 sentence paragraph and
// surfaces in a navigator-styled companion window - so what they were just
// reading stays with them while they read on. The feature spec's shape,
// with the trigger map narrowed to the reader's own hand (reading itself
// never summons the companion):
//   * the toggle lives in the summary navigator's title bar (Recap mode),
//     persisted in "recap.enabled"; unchecking dismisses any open recap,
//   * the condensation cache is keyed by the navigator's WHOLE range - a
//     stretch recapped once never costs a second request (0 ms on return),
//   * the triggers are the reader's own hand and nothing else: F commands
//     the window itself - pressed inside the AI summary windows (the
//     navigator or the companion), never the PDF editor: one press puts
//     it away, the next brings it back over the stretch on screen - and
//     a range move (the steppers, keyboard Left/Right or A/D, a retyped
//     anchor, a span chip) opens a closed window or follows along one
//     already showing, while Recap mode is on and F has not put it away,
//   * the request is one lightweight OpenAI-compatible call at temperature 0
//     over the page's extracted text (PageSummarizer.ExtractRangeAsync),
//   * an already-open window updates in place - pages never stack windows.
//
// One flight at a time: a newer uncached stretch supersedes (cancels) the
// older fetch, because its result could only matter if the reader came back -
// and coming back starts a fresh flight for exactly that stretch. A completed
// result paints only when its stretch is still the one on screen. Everything
// dies with the document: a switch (or close) empties the cache and closes
// the window, so no recap ever names a stretch of the wrong book. The
// UI-thread rule: ShowRecap runs on the UI thread (the range-move path),
// and the flight's awaits resume there too (no ConfigureAwait(false)), so
// every paint is a legal UI touch.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Avalanche.Features.AI;
    using Avalanche.Services;

    internal static class RecapController
    {
        // The condensation cache: (first, last) of the navigator's range ->
        // the 3-4 sentence paragraph. The recap spans the whole stretch the
        // reader is reading, not the single page they just left, so the range
        // pair is the identity. A cache hit paints in 0 ms and never spends a
        // request. An empty string is a cached verdict too: this range has no
        // readable text, and re-asking (the extractor, let alone the model)
        // would answer the same. The cache belongs to ONE document -
        // NotifyDocumentChanged empties it.
        private static readonly Dictionary<(int First, int Last), string> Cache = new();

        private static RecapWindow? _window;
        private static bool? _enabled;
        private static CancellationTokenSource? _cts;
        private static int _generation;     // bumped by every newer request/dismiss: stale continuations can't repaint
        private static Task? _flight;       // the in-flight condensation (null when none)
        private static int _flightFirst;    // the stretch the flight condenses
        private static int _flightLast;
        private static bool _suppressed;    // F-dismissed: range moves keep the window down until F summons it back

        /// <summary>Recap mode's persisted state, read lazily so the companion
        /// also works when the summary navigator has not been opened yet.</summary>
        internal static bool Enabled => _enabled ??= ReadEnabled();

        internal static void SetEnabled(bool value)
        {
            _enabled = value;
            try
            {
                AppDataPaths.SetSetting("recap.enabled", value ? "1" : "0");
            }
            catch
            {
                // best-effort
            }

            // A fresh check lifts the F-silence too: turning Recap on means
            // the companion follows the very next range move.
            _suppressed = false;
            if (!value)
            {
                Dismiss();      // unchecking dismisses any recap window that is showing
            }
        }

        private static bool ReadEnabled()
        {
            try
            {
                return AppDataPaths.GetSetting("recap.enabled") == "1";
            }
            catch
            {
                return false;
            }
        }

        // The document changed (switched, closed): the cache and the open window
        // belong to the old book, and a page number without its book is meaningless.
        internal static void NotifyDocumentChanged()
        {
            _suppressed = false;
            Cache.Clear();
            Dismiss();
        }

        // Kill the flight and close the window. The window's own Closed hook
        // clears the field when the reader dismisses it manually.
        internal static void Dismiss()
        {
            _generation++;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _cts = null;
            _flight = null;
            _flightFirst = 0;
            _flightLast = 0;
            if (_window is { } window)
            {
                _window = null;
                try { window.Close(); } catch { /* already closing */ }
            }
        }

        /// <summary>True while a recap window is actually showing. The
        /// navigator's range moves and F are the only things that ever
        /// open one - reading itself never does.</summary>
        internal static bool HasOpenWindow => _window is { IsVisible: true };

        /// <summary>The reader's hand on the companion (the F key, forwarded by
        /// the AI summary windows while one of them holds the focus): one press
        /// puts the window away - and range moves keep it put away, whatever
        /// the navigator's Recap switch says - the next brings it back over
        /// the stretch on screen, a cached range painting in 0 ms. The
        /// navigator's range moves own the summons; F owns the window
        /// itself, with Recap off too.</summary>
        internal static void ToggleWindow(
            MainWindow owner,
            string filePath,
            int pageCount,
            int first,
            int last,
            Func<AiProviderConfig> configProvider,
            Func<string, string> loc)
        {
            if (_window is { IsVisible: true } open && open.DocumentPathEquals(filePath))
            {
                _suppressed = true;
                Dismiss();
                return;
            }

            ShowRecap(owner, filePath, pageCount, first, last, configProvider, loc, manual: true);
        }

        /// <summary>A range move: the reading window moved (the navigator's
        /// steppers, keyboard Left/Right or A/D, a retyped anchor, a span chip),
        /// and its whole stretch (<paramref name="first"/>..<paramref name="last"/>)
        /// deserves its memory bridge. Shows (or retargets) the companion, serves a
        /// cache hit at once, otherwise starts - or keeps - the condensation flight.
        /// A manual summon (F) bypasses the mode gate and lifts the F-silence.</summary>
        internal static void ShowRecap(
            MainWindow owner,
            string filePath,
            int pageCount,
            int first,
            int last,
            Func<AiProviderConfig> configProvider,
            Func<string, string> loc,
            bool manual = false)
        {
            if (!manual && (!Enabled || _suppressed))
            {
                return;
            }

            first = Math.Clamp(first, 1, Math.Max(1, pageCount));
            last = Math.Clamp(last, first, Math.Max(1, pageCount));

            // A window left over from another book goes first: its title, its
            // cache identity and its flight all belong to the old document.
            if (_window is { } open && !open.DocumentPathEquals(filePath))
            {
                Dismiss();
            }

            bool created = false;
            if (_window is null)
            {
                var window = new RecapWindow(owner, filePath, pageCount, loc);
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(window, _window))
                    {
                        _window = null;
                    }
                };
                _window = window;
                created = true;
            }

            // Retarget BEFORE the first paint (a fresh window paints once, with
            // the right title and the loading line already in place) and before
            // the visible repaint of a window that is already up.
            _window!.NavigateTo(first, last);
            if (created)
            {
                _window.Show();
            }

            if (Cache.TryGetValue((first, last), out string? ready))
            {
                // 0 ms: the page was condensed before. Non-empty paints the
                // paragraph; the cached empty verdict repaints itself - either
                // way the model is never asked twice for the same page.
                if (ready.Length > 0)
                {
                    _window.ShowRecapText(first, last, ready);
                }
                else
                {
                    _window.ShowRecapEmpty(first, last);
                }

                return;
            }

            StartCondensation(filePath, first, last, configProvider, loc);
        }

        // ── v1.19.30: the digest's own recap, before the buffer ────────────
        // The summary navigator's post-digest queue condenses the stretch the
        // reader is reading BEFORE the background buffer spends the model, and
        // narrates the wait in the status line. This is the condensation that
        // queue awaits: same cache, same prompts, same readability gate - but
        // the companion's window discipline is untouched (reading itself never
        // summons the window; a fresh cache just means the next range move or F
        // paints in 0 ms). True = the stretch has a cached verdict of some kind.
        internal static async Task<bool> TryCondenseQuietlyAsync(
            string filePath,
            int pageCount,
            int first,
            int last,
            Func<AiProviderConfig> configProvider,
            Func<string, string> loc)
        {
            first = Math.Clamp(first, 1, Math.Max(1, pageCount));
            last = Math.Clamp(last, first, Math.Max(1, pageCount));
            if (Cache.TryGetValue((first, last), out _))
            {
                return true;   // condensed before: 0 ms, no request
            }

            // The companion's own flight for this exact stretch is already
            // running: wait for it instead of spending the same request twice.
            if (_flightFirst == first && _flightLast == last && _flight is { IsCompleted: false })
            {
                try { await _flight.ConfigureAwait(true); }
                catch { /* the flight's own verdict already landed in the cache */ }
                return Cache.ContainsKey((first, last));
            }

            try
            {
                string rangeText = await PageSummarizer.ExtractRangeAsync(
                        filePath, first, last, CancellationToken.None)
                    .ConfigureAwait(true);
                if (!HasReadableText(rangeText))
                {
                    Cache[(first, last)] = string.Empty;    // the verdict is cached too
                    return true;
                }

                string condensed = (await PageSummarizer.CondenseAsync(
                        configProvider(),
                        CondenseSystemPrompt(ReadLanguage()),
                        "Text to condense:\n" + rangeText,
                        CancellationToken.None)
                    .ConfigureAwait(true)).Trim();
                if (condensed.Length == 0)
                {
                    return false;
                }

                Cache[(first, last)] = condensed;
                return true;
            }
            catch
            {
                // a recap that will not condense is exactly the failure the
                // recap window's own flight reports when the reader arrives;
                // the queue just moves on to the buffer.
                return false;
            }
        }

        private static void StartCondensation(
            string filePath,
            int first,
            int last,
            Func<AiProviderConfig> configProvider,
            Func<string, string> loc)
        {
            // Bounced back to a page whose condensation is still running? Keep
            // waiting on the same flight - a second request for the same stretch
            // would spend the same money to arrive later.
            if (_flightFirst == first && _flightLast == last && _flight is { IsCompleted: false })
            {
                return;
            }

            // Supersede: one flight at a time. A newer page turn cancels the
            // older fetch (the superseded source is left for GC, the digest's
            // own rule) and starts fresh for the stretch on screen now.
            _generation++;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _cts = new CancellationTokenSource();
            _flightFirst = first;
            _flightLast = last;
            _flight = RunCondensationAsync(
                filePath, first, last, configProvider(), loc, _cts.Token, _generation);
        }

        private static async Task RunCondensationAsync(
            string filePath,
            int first,
            int last,
            AiProviderConfig config,
            Func<string, string> loc,
            CancellationToken ct,
            int generation)
        {
            try
            {
                // The range's own text, extracted the same way the navigator's
                // digest is: normalized markdown with the [p. N] anchors.
                string rangeText = await PageSummarizer.ExtractRangeAsync(filePath, first, last, ct)
                    .ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();
                if (!HasReadableText(rangeText))
                {
                    Cache[(first, last)] = string.Empty;    // the verdict is cached too
                    if (generation == _generation)
                    {
                        _window?.ShowRecapEmpty(first, last);
                    }

                    return;
                }

                string condensed = (await PageSummarizer.CondenseAsync(
                        config,
                        CondenseSystemPrompt(ReadLanguage()),
                        "Text to condense:\n" + rangeText,
                        ct)
                    .ConfigureAwait(true)).Trim();
                if (condensed.Length == 0)
                {
                    if (generation == _generation)
                    {
                        _window?.ShowRecapFailed(
                            first, last, loc("Str_SummaryNoText"));
                    }

                    return;
                }

                Cache[(first, last)] = condensed;
                if (generation == _generation)
                {
                    _window?.ShowRecapText(first, last, condensed);
                }
            }
            catch (OperationCanceledException)
            {
                // superseded or dismissed: nothing to paint
            }
            catch (Exception ex)
            {
                if (generation == _generation)
                {
                    _window?.ShowRecapFailed(
                        first, last, PageSummarizer.FriendlyError(ex));
                }
            }
        }

        // Marker-aware readability gate: the extractor prefixes "[p. N]" - and
        // the 'p' inside the marker is a letter, so a bare page-number leaf
        // (a scanned leaf, a chart page) would pass a naive letters count.
        // Strip the markers, then demand a small floor of real prose before
        // spending a request.
        private static readonly Regex PageMarker =
            new(@"\[{1,2}p\.\s*\d+\]{1,2}", RegexOptions.Compiled);

        private static bool HasReadableText(string text)
        {
            int letters = 0;
            foreach (char c in PageMarker.Replace(text, string.Empty))
            {
                if (char.IsLetter(c) && ++letters >= 40)
                {
                    return true;
                }
            }

            return false;
        }

        // The condensation order, verbatim from the feature spec: one short
        // paragraph, essential points only, flowing prose, the reader's
        // language. The page text rides as the user message ("Text to
        // condense:"), the order itself as the system message - the standard
        // two-message shape the provider bridge already speaks.
        private static string CondenseSystemPrompt(string language) =>
            "Condense the following text into a single short paragraph of 3-4 sentences.\n" +
            "Capture only the essential points — the most important facts, findings, events, or takeaways.\n" +
            "Drop all detail, examples, and elaboration.\n" +
            "Write it as flowing prose, not bullets.\n" +
            "\n" +
            $"Respond in {language} only.";

        // The recap answers in the reader's chosen digest language (the
        // navigator's language dropdown persists it); English until they say
        // otherwise.
        private static string ReadLanguage()
        {
            try
            {
                string? lang = AppDataPaths.GetSetting("summary.lang");
                if (!string.IsNullOrWhiteSpace(lang))
                {
                    return lang;
                }
            }
            catch
            {
                // best-effort
            }

            return "English";
        }
    }
}
