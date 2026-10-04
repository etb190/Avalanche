using System.Threading;
using Avalanche.Services;

namespace Avalanche.Features
{
    /// <summary>
    /// The document-search state machine: runs SearchService over the working file, keeps the
    /// per-page rect map and the flat reading-ordered match list, and steps the match cursor.
    /// Moved out of Search.cs (MainWindow) in the KillerUI refactor.
    ///
    /// Holds no controls. Talks to the window only through <see cref="ISearchHost"/>; the search
    /// bar UI, debounce, and the highlight painting stay in the shell half (Shell/Search.cs).
    /// </summary>
    internal sealed class SearchController
    {
        private readonly ISearchHost _host;
        private readonly SearchService _searchService = new();

        internal SearchController(ISearchHost host) => _host = host;

        // Whole-document search results (PDF-space rects per page). Settable references on
        // purpose: Tabs.cs parks these per tab and swaps them back on a tab switch, exactly as
        // it did when they were MainWindow fields.
        internal Dictionary<int, List<(double left, double bottom, double right, double top)>> AllSearchRects { get; set; } = [];
        internal List<int> ResultPages { get; set; } = [];
        internal int PageCursor { get; set; } = -1;

        // Flat, reading-ordered list of every match (page + rect) so Enter steps word-by-word rather
        // than page-by-page; _matchCursor indexes it and that match is drawn with extra emphasis.
        private readonly List<(int page, double left, double bottom, double right, double top)> _matches = [];
        private int _matchCursor = -1;
        private int _totalHits;

        // The async scan's cancellation + staleness: a newer query (or a closed
        // bar) cancels the in-flight walk, and the generation counter makes any
        // late batch or completion from the old run drop silently.
        private CancellationTokenSource? _searchCts;
        private int _runGen;

        /// <summary>True while any page has result rects - the F3 and repaint-on-page-change gate.</summary>
        internal bool HasResults => AllSearchRects.Count > 0;

        /// <summary>The emphasized match, or null when the cursor is not on one.</summary>
        internal (int page, double left, double bottom, double right, double top)? CurrentMatch =>
            _matchCursor >= 0 && _matchCursor < _matches.Count ? _matches[_matchCursor] : null;

        internal bool TryGetPageRects(int page,
            out List<(double left, double bottom, double right, double top)> rects) =>
            AllSearchRects.TryGetValue(page, out rects!);

        internal IEnumerable<int> PagesWithResults => AllSearchRects.Keys;

        /// <summary>The query-too-short reset (search box text dropped under 2 chars).</summary>
        internal void ClearMatches()
        {
            CancelActiveSearch();
            AllSearchRects.Clear();
            ResultPages.Clear();
            _matches.Clear();
            _matchCursor = -1;
            PageCursor = -1;
        }

        /// <summary>The new/closed-document reset - exactly the three things FileOperations
        /// cleared when these were fields (the match list is rebuilt by the next Run).</summary>
        internal void ClearPageResults()
        {
            CancelActiveSearch();
            AllSearchRects.Clear();
            ResultPages.Clear();
            PageCursor = -1;
        }

        /// <summary>
        /// The async scan: the PdfPig walk parks in Task.Run so the UI never
        /// freezes on a big book (the old synchronous Run held the whole message
        /// pump for 5-25s), a newer query or a closed bar cancels the in-flight
        /// walk at the next page boundary, and pages with hits stream back in
        /// batches - the reader sees matches appear while the rest of the
        /// document is still scanning. The cursor jump + final counter land
        /// once, on completion.
        /// </summary>
        internal async Task RunAsync(string query)
        {
            try { _searchCts?.Cancel(); } catch (ObjectDisposedException) { }
            _searchCts?.Dispose();
            var cts = _searchCts = new CancellationTokenSource();
            int gen = ++_runGen;

            _host.ClearHighlights();
            AllSearchRects.Clear();
            ResultPages.Clear();
            _matches.Clear();
            _matchCursor = -1;
            PageCursor = -1;

            if (string.IsNullOrWhiteSpace(query) || _host.CurrentFile is null)
            {
                _host.SetResultText("");
                return;
            }

            _host.SetResultText("Searching…");

            string file = _host.CurrentFile;
            try
            {
                await Task.Run(() =>
                {
                    SearchService.Search(file, query, cts.Token,
                        onPageHits: (page, hits) =>
                            _host.PostToUi(() => MergeBatch(gen, page, hits)));
                });

                if (gen != _runGen) return;     // superseded while scanning
                CompleteSearch();
            }
            catch (OperationCanceledException)
            {
                // a newer query (or the closed bar) owns the field now
            }
            catch
            {
                if (gen == _runGen)
                {
                    _host.SetResultText(_host.Loc("Str_Search_Error"));
                }
            }
        }

        // One page's hits landed while the scan still runs: park the rects,
        // extend the reading-ordered match list, and repaint so the partial
        // results show at once. A batch from a superseded run is dropped.
        private void MergeBatch(int gen, int page,
            IReadOnlyList<(double Left, double Bottom, double Right, double Top)> hits)
        {
            if (gen != _runGen || AllSearchRects.ContainsKey(page)) return;

            AllSearchRects[page] = [.. hits];
            ResultPages.Add(page);
            // Reading order within the page: top-to-bottom, then left-to-right.
            foreach (var (left, bottom, right, top) in hits.OrderByDescending(r => r.Top).ThenBy(r => r.Left))
                _matches.Add((page, left, bottom, right, top));

            _host.RepaintHighlights();
            _host.SetResultCount(_matches.Count + " …", null);
        }

        // The scan finished under this generation: flatten done (MergeBatch kept
        // the reading order), pick the cursor and jump to the first match.
        private void CompleteSearch()
        {
            if (_matches.Count == 0)
            {
                _host.SetResultText(_host.Loc("Str_Search_NoMatches"));
                return;
            }

            // Start at the first match on or after the current page.
            int startPage = _host.CurrentPageIndex;
            _matchCursor = _matches.FindIndex(m => m.page >= startPage);
            if (_matchCursor < 0) _matchCursor = 0;

            GoToCurrentMatch();
        }

        /// <summary>Cancels any in-flight scan - the query dropped under two
        /// characters, the bar closed, a document switch. The RunAsync awaiter
        /// wakes cancelled and bails silently.</summary>
        internal void CancelActiveSearch()
        {
            _runGen++;
            try { _searchCts?.Cancel(); } catch (ObjectDisposedException) { }
            _searchCts?.Dispose();
            _searchCts = null;
        }

        internal void Next()
        {
            if (_matches.Count == 0) return;
            _matchCursor = (_matchCursor + 1) % _matches.Count;
            GoToCurrentMatch();
        }

        internal void Prev()
        {
            if (_matches.Count == 0) return;
            _matchCursor = (_matchCursor - 1 + _matches.Count) % _matches.Count;
            GoToCurrentMatch();
        }

        // Navigates to the current match's page (if needed), updates the counter, and repaints
        // highlights with the current match emphasized. Shared by Run and Next/Prev.
        private void GoToCurrentMatch()
        {
            if (_matchCursor < 0 || _matchCursor >= _matches.Count) return;
            int targetPage = _matches[_matchCursor].page;
            PageCursor = ResultPages.IndexOf(targetPage);   // keep the persisted page-cursor sane
            UpdateStatus();
            if (_host.CurrentPageIndex != targetPage)
                _host.GoToPage(targetPage);
            _host.RepaintHighlights();
        }

        // Compact count ("12 / 73" = current match / total matches); page breakdown in the tooltip.
        private void UpdateStatus()
        {
            if (_matches.Count == 0)
            {
                _host.SetResultCount(_host.Loc("Str_Search_NoMatches"), null);
                return;
            }
            int pages = ResultPages.Count;
            _host.SetResultCount($"{_matchCursor + 1} / {_matches.Count}",
                string.Format(_host.Loc("Str_Search_Summary"), _matches.Count, pages));
        }
    }
}
