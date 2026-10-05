using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Avalanche.Services;
using Microsoft.Web.WebView2.Core;

namespace Avalanche.Controls
{
    /// <summary>
    /// The lightweight in-app browser (v1.19.0). One WebView2 wrapped in reading chrome:
    /// back / forward / refresh / home, an omnibox that searches when the text is not an
    /// address, the curated academic quick-access chips, and the PDF hand-off that sends
    /// every PDF the page offers into the reader instead of the browser's own viewer:
    /// nothing is ever cancelled, so the request a site sees is always Chromium's own -
    /// downloads stream through the engine straight into the reader's temp area, and a
    /// document that still reaches Chromium's viewer is pulled back out with the bytes
    /// the engine itself received, then a fetch from inside the page, then the
    /// credentialed out-of-process fetch as the last resort, every candidate checked
    /// for a real PDF header before the reader hears of it. A failed hand-off is never
    /// remembered, so a blocked link can always be retried, and no download is ever
    /// interrupted by a suspend. v1.19.7: the capture no longer asks whose frame an
    /// answer was for - every application/pdf response is captured wherever it came
    /// from, the engine is asked to hand PDFs over as downloads instead of rendering
    /// them, and every status word takes itself down. v1.19.8: a verified pdf answer
    /// starts the hand-off wherever it came from, a challenge page gets its engine
    /// back whatever status code it carries, and a handed-off document leaves no tab
    /// behind - the browser settles while the engine is still awake. v1.19.9:
    /// the capture grows its last resort - the print of the very page the
    /// viewer rendered, so the one document every fetch layer loses still
    /// reaches the reader. v1.19.10: the print asks the address instead of a
    /// frame that lies, and a tiny riding extension adds the one-click hand -
    /// a button on the viewer itself that downloads the document the proven
    /// way. v1.19.11: the hand-off's word becomes a light in the toolbar row -
    /// spinner, check, cross - the reader keeps its own bookmarks where the
    /// curated dial chips used to sit, and the rail's tabs hold their place:
    /// a revisit refreshes a card, never reshuffles the row. Everything
    /// is lazy: no WebView2 process exists
    /// until the pane is first shown, and TrySuspendAsync hands the engine's memory and GPU
    /// surfaces back to Windows whenever the pane hides again.
    /// </summary>
    public partial class WebBrowserControl : UserControl
    {
        /// <summary>A local PDF is ready for the reader. Raised on the UI thread.</summary>
        public event Action<string>? PdfRequested;

        /// <summary>The browser tab's label changed - a page title arrived, or a
        /// navigation fell back to the address host. Raised on the UI thread.</summary>
        public event Action<string>? TitleChanged;

        private const string HomePage = "https://duckduckgo.com/";

        private bool _initStarted;
        private string? _pendingUrl;
        private string? _lastPageUrl;

        // Only the hand-offs currently in flight are remembered, and only so two
        // completions cannot race: the moment one lands - or fails - the URL is free
        // again. A blocked link is never blacklisted; the next click is a real retry.
        private readonly HashSet<string> _pdfInFlight = new(StringComparer.Ordinal);

        // The bytes of the most recent application/pdf response the engine itself
        // received (WebResourceResponseReceived), offered to a hand-off before any
        // re-fetch is attempted. Fresh with every navigation.
        private TaskCompletionSource<byte[]?>? _pdfCapture;

        // Downloads the engine is still writing: suspension waits until the last one
        // lands, because TrySuspendAsync mid-download is how a "damaged" PDF is born.
        private int _activeDownloads;

        // v1.19.5: the early visual guard. Chromium's viewer must never paint: a
        // main-frame response that announces application/pdf (or a pdf-shaped
        // address) blanks the engine the instant that is knowable, and the bytes
        // ride the capture that is already copying the response. The guard belongs
        // to one navigation; every exit path restores the engine.
        private bool _browserGuarded;
        private bool _earlyHandoff;      // one early hand-off attempt per navigation
        private string? _mainNavUri;     // the address the top-level navigation started from
        private bool _navCompleted;      // the last navigation landed successfully - a failed
                                         // early hand-off owes the completed fallback at once

        // v1.19.11: the toolbar light's one state at a time - a spinner while the
        // capture runs, a green check or a red cross for its verdict. The turning
        // is a DispatcherTimer, the same idiom the transient word used.
        private enum ActivityState { Hidden, Spinning, Success, Failed }
        private ActivityState _activity = ActivityState.Hidden;
        private System.Windows.Threading.DispatcherTimer? _spinTimer;
        private System.Windows.Threading.DispatcherTimer? _activityTakeDown;

        // v1.19.11: the reader's own bookmarks - one JSON file in the app's data
        // root, one collection the chips wrap, and the star that mirrors whether
        // the page on screen is already saved. The flyout edits one bookmark at
        // a time; null means the next save is a new one.
        private BookmarkVm? _bookmarkEditing;

        // v1.19.11: false until the engine exists. Without it there is no toolbar
        // light worth dressing a word in, so the strip keeps speaking.
        private bool _engineReady;

        public WebBrowserControl()
        {
            InitializeComponent();
            // A transparent engine over the themed card: no white flash while the page
            // loads, and the blank state belongs to the theme instead of Chromium.
            Browser.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            // Ctrl+T is the + button's keyboard face (the tooltip says so): the control
            // tunnels the gesture wherever the browser's own surface holds the focus -
            // the omnibox above all.
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    OpenNewTab();
                }
            };
            // v1.19.11: the chips load before the pane is ever shown - the reader
            // opens the browser onto its own saved pages, never an empty row.
            LoadBookmarks();
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────────────────

        /// <summary>The pane just became visible: resume a suspended engine, or build one -
        /// the first open is the only moment the browser process is ever created.</summary>
        public void OnPaneShown()
        {
            if (!_initStarted)
            {
                _ = EnsureReadyAsync();
            }
            else
            {
                try { Browser.CoreWebView2?.Resume(); } catch { /* busy or gone: the next show retries */ }
            }
            // v1.19.5: a reopened pane never sits on Chromium's viewer - if the
            // last navigation ended on a document, step back to what offered it.
            DropVisualGuard(restore: true);
            try
            {
                if (LooksLikePdf(Browser.Source?.ToString())) RetreatFromInlinePdf();
            }
            catch { /* a source that refuses probing stays as it is */ }
            // The address bar takes the caret, browser-style - deferred once so the
            // first show (pane still measuring) cannot silently drop the focus.
            Dispatcher.BeginInvoke(
                () => { OmniBox.Focus(); OmniBox.SelectAll(); },
                System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>The pane just hid: suspend the engine so an idle browser costs nothing
        /// but a hwnd. Fire-and-forget on purpose; a refused suspend is retried next hide.</summary>
        public void OnPaneHidden()
        {
            _ = SuspendAsync();
        }

        /// <summary>The window is going away: stop the browser process cleanly.</summary>
        public void ShutdownForExit()
        {
            try { Browser.Dispose(); } catch { /* best effort during shutdown */ }
        }

        private async Task SuspendAsync()
        {
            if (_activeDownloads > 0) return;   // a file is being written; suspension can wait
            try
            {
                if (Browser.CoreWebView2 is { } core)
                    await core.TrySuspendAsync();
            }
            catch
            {
                // Suspension is a courtesy, never a requirement.
            }
        }

        private async Task EnsureReadyAsync()
        {
            if (_initStarted) return;
            _initStarted = true;
            CoreWebView2Environment env;
            try
            {
                string dataDir = Path.Combine(AppDataPaths.UserRoot, "WebView2Data");
                Directory.CreateDirectory(dataDir);
                // v1.19.10: the environment wears one switch now - extensions
                // enabled - because the hand-off's last line of defense rides in
                // one: a tiny helper extension that puts an "open in Avalanche"
                // button on the viewer itself. The v1.19.9 retirement stands: no
                // Chromium feature guesses ride along - a flag the runtime does
                // not know is dropped in silence, and this one is a documented
                // environment option the SDK speaks.
                CoreWebView2EnvironmentOptions options = new()
                {
                    AreBrowserExtensionsEnabled = true,
                };
                env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
                await Browser.EnsureCoreWebView2Async(env);
            }
            catch
            {
                _initStarted = false;   // the remedy (installing the runtime) can be retried live
                ShowStatus(TryLoc("Str_Web_RuntimeMissing"));
                return;
            }

            HideStatus();
            _engineReady = true;   // the light exists from here on; the strip retires
            CoreWebView2 core = Browser.CoreWebView2!;
            // v1.19.7: the built-in viewer's toolbar is pinned explicitly; the
            // viewer's real ban lives in the capture - a document that still
            // reaches the viewer is pulled straight back out before it can be a
            // PDF surface this app never promised, and v1.19.9 gives that pull
            // a print for the one document the fetch layers all lose.
            try { core.Settings.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.None; }
            catch { /* a runtime without the setting keeps its defaults */ }
            core.DownloadStarting += OnDownloadStarting;
            core.WebResourceResponseReceived += OnWebResourceResponseReceived;
            core.NewWindowRequested += OnNewWindowRequested;
            core.HistoryChanged += (_, _) => RefreshHistoryButtons();
            Browser.NavigationStarting += OnNavigationStarting;
            Browser.NavigationCompleted += OnNavigationCompleted;
            core.DocumentTitleChanged += (_, _) => RaiseTitleChanged();
            // v1.19.10: the riding helper - a two-kilobyte extension that wears
            // the "open in Avalanche" button on the viewer itself - is loaded
            // once per profile; loading it is a courtesy, never a requirement.
            try
            {
                string extPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Resources", "WebExtensions", "avalanche-pdf");
                if (Directory.Exists(extPath))
                    await core.Profile.AddBrowserExtensionAsync(extPath);
            }
            catch { /* extension loading is a courtesy */ }
            core.WebMessageReceived += OnWebMessageReceived;
            SetChromeEnabled(true);
            NavigateTo(_pendingUrl ?? HomePage);
            _pendingUrl = null;
            RefreshBookmarkButton();
        }

        // ── Navigation ────────────────────────────────────────────────────────────────────

        private void WebBackBtn_Click(object sender, RoutedEventArgs e)
        {
            try { Browser.CoreWebView2?.GoBack(); } catch { /* nothing to retrace yet */ }
        }

        private void WebForwardBtn_Click(object sender, RoutedEventArgs e)
        {
            try { Browser.CoreWebView2?.GoForward(); } catch { /* nothing to retrace yet */ }
        }

        private void WebRefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Browser.CoreWebView2 is null) _ = EnsureReadyAsync();
                else Browser.Reload();
            }
            catch { /* a reload that throws is the next one's problem */ }
        }

        private void WebHomeBtn_Click(object sender, RoutedEventArgs e) => NavigateTo(HomePage);

        // ── Bookmarks (v1.19.11) - the click surface ─────────────────────────────────
        // The dial chips retired; their row wears the reader's saved pages. A chip
        // click navigates, a right-click edits the name or deletes the chip, the
        // star saves or forgets the page on screen, and the flyout holds the one
        // name field the whole system needs. The file behind them is
        // bookmarks.json in the app's data root - name, url, favicon.

        private void BookmarkChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string url } && url.Length > 0) NavigateTo(url);
        }

        private void BookmarkEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: BookmarkVm vm }) OpenBookmarkFlyout(vm);
        }

        private void BookmarkDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: BookmarkVm vm })
            {
                Bookmarks.Remove(vm);
                PersistBookmarks();
                RefreshBookmarksSurface();
                RefreshBookmarkButton();
            }
        }

        /// <summary>The star: a filled one forgets the page on screen; an outlined
        /// one opens the naming flyout, prefilled with the page's own title.</summary>
        private void WebBookmarkBtn_Click(object sender, RoutedEventArgs e)
        {
            string url = Browser.Source?.ToString() ?? string.Empty;
            BookmarkVm? existing = FindBookmark(url);
            if (existing is not null)
            {
                Bookmarks.Remove(existing);
                PersistBookmarks();
                RefreshBookmarksSurface();
                RefreshBookmarkButton();
                return;
            }
            if (url.Length == 0) return;
            OpenBookmarkFlyout(null);
        }

        private void WebBookmarkSave_Click(object sender, RoutedEventArgs e) => CommitBookmarkFlyout();

        private void WebBookmarkNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; CommitBookmarkFlyout(); }
            else if (e.Key == Key.Escape) { e.Handled = true; WebBookmarkPopup.IsOpen = false; }
        }

        // The omnibox: Enter commits (address when it looks like one, search when it does
        // not), Escape hands the text back to the page and returns to the web.
        private void OmniBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                NavigateTo(ParseInput(OmniBox.Text));
                Browser.Focus();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SyncOmniFromBrowser();
                Browser.Focus();
            }
        }

        private void OmniBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            OmniHint.Visibility = string.IsNullOrEmpty(OmniBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>An absolute http(s) address goes as-is; a bare www. host gains the
        /// scheme; anything else is a search - the reading-browser default.</summary>
        internal static string ParseInput(string raw)
        {
            string text = raw.Trim();
            if (text.Length == 0) return HomePage;
            if (Uri.TryCreate(text, UriKind.Absolute, out Uri? abs)
                && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                return abs.ToString();
            if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate("https://" + text, UriKind.Absolute, out Uri? www))
                return www.ToString();
            return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);
        }

        private void NavigateTo(string url)
        {
            if (Browser.CoreWebView2 is null)
            {
                _pendingUrl = url;   // the first page waits for the engine, never the reverse
                _ = EnsureReadyAsync();
                return;
            }
            try { Browser.CoreWebView2.Navigate(url); }
            catch { /* a navigation that throws is the next one's problem */ }
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            SyncOmniFromBrowser();
            RefreshBookmarkButton();   // v1.19.11: the star follows the page on screen
            RaiseTitleChanged();
            _lastPageUrl = Browser.Source?.ToString();
            if (e.IsSuccess)
            {
                _navCompleted = true;   // a straggling early hand-off can owe the fallback now
                _ = CatchInlinePdfAsync();
            }
            else
            {
                DropVisualGuard(restore: true);   // a dead navigation never strands a blanked engine
                HideStatus();   // v1.19.7: nor a "Fetching..." strip
            }
            _ = UpdateTabCardAsync();   // the sidebar gallery's preview for this view
        }

        /// <summary>The one PDF the browser is allowed to show is none. A link no rule can
        /// see coming - /getpdf?id=..., a redirect that ends in a document - starts
        /// rendering in Chromium's own viewer; the moment the page finishes, the document
        /// is pulled back out with the browser's own credentials - the bytes the engine
        /// itself received, then a fetch from inside the page, then the credentialed
        /// out-of-process fetch - checked, and handed to the reader while the browser
        /// steps back to the page that offered it. Nothing is remembered about a failure:
        /// a hand-off only guards the race it is running.</summary>
        private async Task CatchInlinePdfAsync()
        {
            bool handed = false;   // the document reached the reader
            try
            {
                CoreWebView2? core = Browser.CoreWebView2;
                if (core is null) return;
                string type = await core.ExecuteScriptAsync("document.contentType");
                string plain = type.Trim('"');
                string url = Browser.Source?.ToString() ?? string.Empty;
                // v1.19.7: the probe's word is not final. Chromium's viewer DOM is not
                // a document - the expression comes back "null" or refuses to run - and
                // an embed wrapper answers text/html. When the address itself is
                // pdf-shaped, "null" means the viewer is on screen and the document
                // still comes out: the capture, the page's own fetch, the credentialed
                // one - whatever answers with real PDF bytes.
                // v1.19.8: a viewer DOM IS Chromium's own pdf plugin - ordinary
                // pages always answer text/html - so it is a candidate on its own
                // word now, never gated behind a pdf-shaped address: the
                // viewcontent.cgi routes and the query-string repositories ride it
                // too. A capture the engine has already filled is a candidate as
                // well.
                bool isPdf = plain.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
                bool viewerDom = plain.Length == 0 || plain.Equals("null", StringComparison.OrdinalIgnoreCase);
                bool captured = _pdfCapture is { Task.IsCompleted: true };
                if (!isPdf && !viewerDom && !captured && !LooksLikePdf(url))
                {
                    DropVisualGuard(restore: true);   // an ordinary page: the viewer was never coming
                    return;
                }
                if (url.Length == 0 || !_pdfInFlight.Add(url)) return;
                try
                {
                    ShowStatus(TryLoc("Str_Web_PdfOpening"));
                    byte[]? bytes = await CapturePdfFromBrowserAsync(url);
                    handed = await HandPdfToReaderAsync(url, bytes);
                    // v1.19.8: the retreat is the hand-off's own last step now -
                    // SettleAfterHandoff walks the view back BEFORE the pane hides,
                    // never across the suspension the hide asks for.
                }
                finally
                {
                    _pdfInFlight.Remove(url);   // the outcome is forgotten; only the race is guarded
                    DropVisualGuard(restore: true);   // the engine paints again whatever stayed behind
                    // v1.19.7: the strip never sticks - silence when the reader took
                    // the document, a three-second word when it did not.
                    if (handed) HideStatus();
                    else ShowTransientStatus(TryLoc("Str_Web_PdfBlocked"));
                }
            }
            catch
            {
                // v1.19.7: a probe that stumbled still owes the engine its paint and
                // the strip its silence - nothing here may strand either.
                DropVisualGuard(restore: true);
                HideStatus();
            }
        }

        private void RetreatFromInlinePdf()
        {
            try
            {
                CoreWebView2? core = Browser.CoreWebView2;
                if (core is null) return;
                if (core.CanGoBack) core.GoBack();
                else NavigateTo(HomePage);
            }
            catch { /* the retreat is a courtesy, never a requirement */ }
        }

        /// <summary>The pdf-shaped-address test for the EARLY VISUAL GUARD only -
        /// it never cancels a navigation. A .pdf suffix or a publisher route
        /// (/pdf/&lt;id&gt;, a trailing /pdf, /getpdf?id=) raises the guard a beat
        /// before the response headers speak; a false positive costs one blanked
        /// moment, and the header-driven stand-down gives the page straight back.</summary>
        internal static bool LooksLikePdf(string? url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? u))
                return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
                return false;
            string path = Uri.UnescapeDataString(u.AbsolutePath);
            int jid = path.IndexOf(';');   // jsessionid trailings ride the path
            if (jid >= 0) path = path[..jid];
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return true;
            string[] segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segs.Length - 1; i++)
                if (segs[i].Equals("pdf", StringComparison.OrdinalIgnoreCase)) return true;   // /pdf/&lt;id&gt;
            if (segs.Length > 0)
            {
                string last = segs[^1];
                if (last.Equals("pdf", StringComparison.OrdinalIgnoreCase)
                    || last.Equals("getpdf", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Loose same-address test for the early hand-off: the main-frame
        /// signal this SDK's WebResourceResponseReceived cannot give directly, so
        /// the response is matched against the address the navigation started from
        /// (NavigationStarting fires for the top-level document only).</summary>
        private static bool UriEquals(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Blank the engine the instant a PDF is known to be coming: the
        /// status strip says what is happening and Chromium's viewer canvas never
        /// paints. The guard belongs to one navigation; every exit path - the early
        /// hand-off, the completed-navigation probe, a failed navigation - restores
        /// the engine.</summary>
        private void ArmVisualGuard()
        {
            ShowStatus(TryLoc("Str_Web_PdfOpening"));
            if (_browserGuarded) return;
            _browserGuarded = true;
            Browser.Visibility = Visibility.Hidden;
        }

        private void DropVisualGuard(bool restore)
        {
            if (!_browserGuarded) return;
            _browserGuarded = false;
            if (restore) Browser.Visibility = Visibility.Visible;
        }

        /// <summary>The response headers just said the MAIN document is a PDF: the
        /// viewer must never paint, and the engine's own bytes are already copying
        /// (OnWebResourceResponseReceived). Wait for the capture, check the header,
        /// hand the reader the document; on success the browser steps back to the
        /// page that offered the link once the navigation has somewhere to go back
        /// to. A failure stands the guard down and leaves the NavigationCompleted
        /// fallback its three sources.</summary>
        private async void StartEarlyPdfHandoff(string url)
        {
            if (_earlyHandoff || url.Length == 0) return;
            _earlyHandoff = true;
            bool handed = false;   // the reader accepted the document
            try
            {
                if (!_pdfInFlight.Add(url)) return;
                ArmVisualGuard();   // blanks the engine even when the address never looked like a PDF
                byte[]? bytes = await WaitPdfCaptureAsync(TimeSpan.FromSeconds(20));
                if (bytes != null && HasPdfHeader(bytes))
                {
                    // v1.19.8: no delayed retreat across the pane's suspension -
                    // the hand-off itself settles the browser while the engine is
                    // still awake: guard down, view back, no pdf tab left behind.
                    handed = await HandPdfToReaderAsync(url, bytes);   // the pane steps aside; the reader tab opens
                }
                else
                {
                    // v1.19.6: a failed early hand-off owes the completed-navigation
                    // fallback its turn - immediately when the navigation already
                    // landed, otherwise the moment it does (OnNavigationCompleted
                    // runs it for a navigation still in flight).
                    _pdfInFlight.Remove(url);
                    DropVisualGuard(restore: true);
                    HideStatus();
                    if (_navCompleted) _ = CatchInlinePdfAsync();
                }
            }
            finally
            {
                _earlyHandoff = false;
                // v1.19.7: the strip's fate is settled once, here, whatever the
                // outcome - silence when the reader took the document or the
                // fallback is taking its turn, a three-second word when the fetch
                // failed and no fallback is coming.
                if (handed || _navCompleted) HideStatus();
                else ShowTransientStatus(TryLoc("Str_Web_PdfBlocked"));
            }
        }

        /// <summary>The tab's title: the page's own when it has one, otherwise the
        /// address host. Empty stays silent - the tab keeps whatever it wore.</summary>
        private void RaiseTitleChanged()
        {
            CoreWebView2? core = Browser.CoreWebView2;
            if (core is null) return;
            string title = core.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title)) title = Browser.Source?.Host ?? string.Empty;
            if (title.Length == 0) return;
            // v1.19.7: the strip tab wears its title the moment it arrives - the
            // card for the view the browser is on, not whatever rang before it.
            try
            {
                string url = Browser.Source?.ToString() ?? string.Empty;
                WebTabCardVm? card = Tabs.FirstOrDefault(t => UriEquals(t.Url, url));
                if (card is not null) card.Title = title;
            }
            catch { /* a gallery hiccup never disturbs the title event */ }
            TitleChanged?.Invoke(title);
        }

        private void SyncOmniFromBrowser()
        {
            if (OmniBox.IsKeyboardFocused) return;   // never fight the reader's typing
            OmniBox.Text = Browser.Source?.ToString() ?? string.Empty;
            OmniBox.CaretIndex = OmniBox.Text.Length;
        }

        private void RefreshHistoryButtons()
        {
            CoreWebView2? core = Browser.CoreWebView2;
            WebBackBtn.IsEnabled = core is { CanGoBack: true };
            WebForwardBtn.IsEnabled = core is { CanGoForward: true };
        }

        private void SetChromeEnabled(bool on)
        {
            WebBackBtn.IsEnabled = WebForwardBtn.IsEnabled = WebRefreshBtn.IsEnabled
                = WebHomeBtn.IsEnabled = WebBookmarkBtn.IsEnabled = on;
            if (on) RefreshHistoryButtons();
        }

        // ── The PDF hand-off ──────────────────────────────────────────────────────────────
        // A PDF is never rendered by the browser, and the browser's own request is never
        // cancelled: navigations and downloads ride Chromium's network stack, the only
        // one a CDN or an anti-bot wall will trust. A download streams through
        // DownloadStarting straight into the reader's temp area; a document that reaches
        // Chromium's viewer is pulled back out - the bytes the engine itself received
        // first, then a fetch from inside the page, then the credentialed
        // out-of-process fetch - and every candidate is checked for a real PDF header
        // before the reader ever hears of it. No failure is remembered, and no download
        // is ever cut short by a suspend. The "open in Avalanche" button hands over the
        // very document when the page is one, and prints the live page when it is not.
        // Every path ends at PdfRequested, which the window turns into an ordinary
        // reader tab.

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // Nothing is cancelled any more: a pdf-shaped address rides the engine's own
            // request to its download or its viewer, and the hand-off catches it there -
            // cancelling here is exactly what invalidated one-time download tokens and
            // left the session's request unanswered. Each navigation starts its capture
            // fresh, with its own in-flight set.
            _pdfCapture = null;
            _pdfInFlight.Clear();
            _earlyHandoff = false;
            _navCompleted = false;
            _mainNavUri = e.Uri;
            // v1.19.5: a pdf-shaped address blanks the engine before its first pixel -
            // the response headers either confirm it (the early hand-off takes over)
            // or stand the guard back down when the real page arrives.
            if (LooksLikePdf(e.Uri)) ArmVisualGuard();
        }

        /// <summary>The document's bytes, from the sources closest to Chromium inward:
        /// first the response the engine itself received for this very document (no
        /// second request exists for a CDN or an anti-bot wall to distrust), then a fetch
        /// running inside the page - the session's own cookies, tokens and TLS
        /// handshake - then the credentialed out-of-process fetch that shares
        /// the engine's cookies and user agent, and finally v1.19.9's last resort:
        /// the print of the very page the viewer rendered. The first source that
        /// yields real PDF bytes wins; null means every one of them failed. The
        /// print is the layer no wall can beat, because it asks for nothing: the
        /// response stream the viewer's own pipeline consumed, the fetch the
        /// viewer's extension DOM refuses, the re-request the anti-bot wall
        /// answers with 403 - all of them lose a document Chromium already has
        /// on screen, and the print takes the screen's bytes.</summary>
        private async Task<byte[]?> CapturePdfFromBrowserAsync(string url)
        {
            byte[]? captured = await WaitPdfCaptureAsync(TimeSpan.FromSeconds(20));
            if (captured != null && HasPdfHeader(captured)) return captured;
            byte[]? scripted = await FetchViaPageScriptAsync();
            if (scripted != null && HasPdfHeader(scripted)) return scripted;
            byte[]? fetched = await FetchBrowserBytesAsync(url);
            if (fetched != null && HasPdfHeader(fetched)) return fetched;
            byte[]? printed = await PrintRenderedPdfAsync();
            if (printed != null && HasPdfHeader(printed)) return printed;
            return null;
        }

        /// <summary>The last resort: print the page the viewer is showing. A document
        /// Chromium's viewer managed to render is ON the screen, and PrintToPdfAsync
        /// writes exactly that render - a valid pdf of the document itself, no
        /// second request any wall can refuse. v1.19.10: the gate asks the
        /// address now, not the frame - the viewer is a two-frame shell whose
        /// OUTER frame answers text/html no matter what it shows, so the old
        /// contentType probe returned a word that sent the print home empty
        /// every time. The evidence the print accepts: the viewer extension's
        /// own chrome-extension address, an address shaped like a document, or
        /// a capture the engine has already built - proof an application/pdf
        /// answer arrived whatever the address spells. Layers 1-3 have all
        /// failed by the time this runs, so the only question left is "is a
        /// document on screen", and the print is its answer. The guard comes
        /// down before the print for the reason the open-in-Avalanche button
        /// already knows: the printed page, not a blanked engine, is the
        /// product.</summary>
        private async Task<byte[]?> PrintRenderedPdfAsync()
        {
            try
            {
                CoreWebView2? core = Browser.CoreWebView2;
                if (core is null) return null;
                string url = Browser.Source?.ToString() ?? string.Empty;
                // The viewer's outer frame lies about its contentType; the
                // address and the capture do not. A capture slot exists only
                // when an application/pdf answer arrived this navigation, so a
                // viewcontent.cgi route whose address never looks like a pdf
                // still reaches the print.
                bool onViewer = url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                    || LooksLikePdf(url)
                    || _pdfCapture is not null;
                if (!onViewer) return null;
                DropVisualGuard(restore: true);   // the print needs the engine alive, not blanked
                string target = TempPdfPath(
                    "print-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".pdf");
                if (!await core.PrintToPdfAsync(target, null)) return null;
                byte[] printed = await File.ReadAllBytesAsync(target);
                try { File.Delete(target); } catch { /* the temp print is a courtesy */ }
                return printed;
            }
            catch { return null; }
        }

        /// <summary>Waits a moment for the engine's own response bytes to finish copying:
        /// a document still streaming is worth waiting for, a capture that never
        /// happened is not.</summary>
        private async Task<byte[]?> WaitPdfCaptureAsync(TimeSpan wait)
        {
            TaskCompletionSource<byte[]?>? capture = _pdfCapture;
            if (capture is null) return null;
            try
            {
                if (await Task.WhenAny(capture.Task, Task.Delay(wait)) != capture.Task) return null;
                return await capture.Task;
            }
            catch { return null; }
        }

        // Runs inside the page's own renderer: the fetch the page itself could make -
        // the session's cookies, tokens and TLS handshake, indistinguishable from the
        // site's own scripts. Returns the document as base64, or an empty string when
        // the page's context refuses to cooperate.
        private const string PageFetchScript =
            "(async()=>{try{const r=await fetch(location.href,{credentials:'include'});" +
            "if(!r.ok)return '';const b=await r.arrayBuffer();const u=new Uint8Array(b);" +
            "let s='';const c=0x8000;for(let i=0;i<u.length;i+=c)" +
            "s+=String.fromCharCode.apply(null,u.subarray(i,i+c));return btoa(s)}catch(e){return ''}})()";

        private async Task<byte[]?> FetchViaPageScriptAsync()
        {
            try
            {
                CoreWebView2? core = Browser.CoreWebView2;
                if (core is null) return null;
                string json = await core.ExecuteScriptAsync(PageFetchScript);
                string? b64 = null;
                try { b64 = System.Text.Json.JsonSerializer.Deserialize<string>(json); }
                catch { /* "null" or malformed JSON means no result */ }
                if (string.IsNullOrEmpty(b64)) return null;
                return Convert.FromBase64String(b64);
            }
            catch { return null; }
        }

        /// <summary>Every road ends here: the bytes are checked for a real PDF header one
        /// last time, written into the reader's temp area, and handed over. The verdict
        /// comes back as the return value - the caller owns the status strip now, so a
        /// failure word outlives the caller's cleanup instead of being wiped by it.</summary>
        private async Task<bool> HandPdfToReaderAsync(string url, byte[]? bytes)
        {
            if (bytes is null || !HasPdfHeader(bytes)) return false;
            try
            {
                string path = Uri.TryCreate(url, UriKind.Absolute, out Uri? u)
                    ? u.AbsolutePath : string.Empty;
                string target = TempPdfPath(SafePdfName(path));
                await File.WriteAllBytesAsync(target, bytes);
                HideStatus();
                // v1.19.8: the browser settles BEFORE the reader hears of the
                // document - guard down, view back on the page that offered it,
                // no pdf tab left behind - all while the engine is still awake,
                // never across the suspension the pane's hide asks for.
                SettleAfterHandoff(url);
                PdfRequested?.Invoke(target);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>v1.19.8: the browser's debt, paid the moment a document reaches the
        /// reader - synchronously, while the engine is still awake and BEFORE
        /// PdfRequested sends the pane off to hide and suspend. The guard comes
        /// down; the view that carried the document steps back to the page that
        /// offered it; and its gallery card goes with it - closed when other views
        /// are open, reset to a live page when it held the tab alone - so a
        /// reopened browser lands on a working page, never on the frozen viewer a
        /// pdf tab used to keep.</summary>
        private void SettleAfterHandoff(string url)
        {
            try { DropVisualGuard(restore: true); } catch { /* a paint flag only */ }
            try
            {
                CoreWebView2? core = Browser.CoreWebView2;
                if (core is null) return;
                if (Tabs.Count > 1)
                {
                    // Other views are open: the document's tab closes and the
                    // browser lands on the next newest view - the search results
                    // or journal index the reader came from.
                    WebTabCardVm? card = Tabs.FirstOrDefault(t => UriEquals(t.Url, url));
                    if (card is not null) { CloseTab(url); return; }
                    foreach (WebTabCardVm t in Tabs) t.IsActive = false;
                    Tabs[0].IsActive = true;
                    try
                    {
                        if (!UriEquals(Browser.Source?.ToString(), Tabs[0].Url))
                            NavigateTo(Tabs[0].Url);
                    }
                    catch { NavigateTo(Tabs[0].Url); }
                    return;
                }
                // The document held the tab alone: step back at once - history
                // first, home when nothing stands behind - and the card that wore
                // the document leaves with it, so nothing lingers as a pdf. The
                // home card is seeded when the retreat lands there and none waits.
                WebTabCardVm? solo = Tabs.FirstOrDefault(t => UriEquals(t.Url, url));
                if (solo is not null) Tabs.Remove(solo);
                RetreatFromInlinePdf();
                if (!core.CanGoBack && !Tabs.Any(t => UriEquals(t.Url, HomePage)))
                {
                    Tabs.Insert(0, new WebTabCardVm(HomePage)
                    {
                        Title = TryLoc("Str_Web_NewTab") ?? "duckduckgo.com",
                        Host = "duckduckgo.com",
                        IsActive = true,
                    });
                }
            }
            catch { /* a settle that stumbles never blocks the hand-off */ }
        }

        /// <summary>The fetch, with the session's own credentials: the cookies the WebView2
        /// engine earned (sign-ins included, HttpOnly ones too), the engine's real user
        /// agent, the page that offered the link as referrer, and honest compression.
        /// Sites that answered the old anonymous fetch with an error page answer this one
        /// the way they answer the user's own browser.</summary>
        private async Task<byte[]> FetchBrowserBytesAsync(string url)
        {
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                                       | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
            };
            CoreWebView2? core = Browser.CoreWebView2;
            Uri target = new Uri(url);
            if (core is not null)
            {
                try
                {
                    foreach (var c in await core.CookieManager.GetCookiesAsync(url))
                    {
                        try
                        {
                            handler.CookieContainer.Add(target, new Cookie(
                                c.Name, c.Value,
                                string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Domain)
                            { Secure = c.IsSecure });
                        }
                        catch
                        {
                            // v1.19.7: a cookie .NET's strict constructor rejects - a
                            // leading-dot domain, a port, a stray character - gets a
                            // second, minimal ride scoped to the target alone, so the
                            // session itself never drops with one bad cookie.
                            try { handler.CookieContainer.Add(target, new Cookie(c.Name, c.Value)); }
                            catch { /* even the minimal cookie refuses; the rest ride along */ }
                        }
                    }
                }
                catch { /* no cookies is a degraded fetch, not a failed one */ }
            }
            using var http = new HttpClient(handler);
            http.Timeout = TimeSpan.FromMinutes(3);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            string ua = FallbackUserAgent;
            try
            {
                if (!string.IsNullOrWhiteSpace(core?.Settings.UserAgent))
                    ua = core.Settings.UserAgent;
            }
            catch { /* a settings hiccup keeps the fallback */ }
            try { req.Headers.UserAgent.ParseAdd(ua); } catch { /* a picky UA stays missing */ }
            if (Uri.TryCreate(_lastPageUrl, UriKind.Absolute, out Uri? referer)
                && referer.Scheme == Uri.UriSchemeHttps)
                req.Headers.Referrer = referer;
            req.Headers.Accept.ParseAdd("application/pdf,application/octet-stream,*/*;q=0.8");
            using HttpResponseMessage resp = await http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync();
        }

        private const string FallbackUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        /// <summary>A real PDF announces itself with %PDF- within the first kilobyte - the
        /// window every reader scans. Anything else never reaches the reader.</summary>
        private static bool HasPdfHeader(byte[] bytes)
        {
            if (bytes is null || bytes.Length < 100) return false;
            int scan = Math.Min(bytes.Length, 1024) - 4;
            for (int i = 0; i < scan; i++)
                if (bytes[i] == 0x25 && bytes[i + 1] == 0x50 && bytes[i + 2] == 0x44
                    && bytes[i + 3] == 0x46 && bytes[i + 4] == 0x2D)
                    return true;
            return false;
        }

        private static bool HasPdfHeaderFile(string path)
        {
            try
            {
                using FileStream fs = File.OpenRead(path);
                byte[] head = new byte[1024];
                int read = fs.Read(head, 0, head.Length);
                return HasPdfHeader(read == head.Length ? head : head[..read]);
            }
            catch { return false; }
        }

        private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            string mime = e.DownloadOperation.MimeType ?? string.Empty;
            string suggested = e.ResultFilePath ?? string.Empty;
            string dlUrl = e.DownloadOperation.Uri ?? string.Empty;   // v1.19.8: the address the document came from
            bool isPdf = mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                || mime.Equals("application/x-pdf", StringComparison.OrdinalIgnoreCase)
                || (mime.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                    && suggested.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

            _activeDownloads++;   // every download, PDF or not: suspension must not strand it
            bool counted = true;
            string? target = null;
            e.DownloadOperation.StateChanged += (s, _) =>
            {
                if (s is not CoreWebView2DownloadOperation op) return;
                if (op.State == CoreWebView2DownloadState.InProgress) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (counted) { counted = false; _activeDownloads--; }
                    if (target is null) return;   // ordinary download: the engine's own handling stays
                    if (op.State == CoreWebView2DownloadState.Completed && HasPdfHeaderFile(target))
                    {
                        HideStatus();
                        // v1.19.8: a document the engine delivered as a download
                        // settles the view too when the browser stands on its
                        // address - a download navigation leaves a blank view
                        // otherwise, dressed as a frozen tab on reopen.
                        try
                        {
                            string view = Browser.Source?.ToString() ?? string.Empty;
                            if (view.Length > 0 && (UriEquals(view, dlUrl) || LooksLikePdf(view)))
                                SettleAfterHandoff(view);
                        }
                        catch { /* a settle that stumbles never blocks the hand-off */ }
                        PdfRequested?.Invoke(target);
                    }
                    else if (op.State == CoreWebView2DownloadState.Completed)
                    {
                        try { File.Delete(target); } catch { /* temp litter is harmless */ }
                        ShowTransientStatus(TryLoc("Str_Web_PdfNotPdf"));   // v1.19.7: the word takes itself down
                    }
                    else
                    {
                        HideStatus();   // interrupted: the status line steps aside
                    }
                });
            };

            if (!isPdf) return;   // ordinary downloads keep the engine's own handling

            e.Handled = true;   // no Edge download flyout for a file the reader is about to eat
            target = TempPdfPath(SafePdfName(suggested.Length > 0 ? suggested : "download.pdf"));
            e.ResultFilePath = target;
            ShowStatus(TryLoc("Str_Web_PdfOpening"));
        }

        /// <summary>The engine's own answer to the page: when it is a PDF document, its
        /// bytes are copied out as they arrive, so the hand-off can use the very response
        /// Chromium negotiated - nothing for a CDN or an anti-bot wall to distrust.
        /// Cache-served answers hide their bytes; the later layers fetch for those.</summary>
        private async void OnWebResourceResponseReceived(
            object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                CoreWebView2HttpResponseHeaders? headers = e.Response.Headers;
                if (headers is null || !headers.Contains("Content-Type")) return;
                string mime = headers.GetHeader("Content-Type") ?? string.Empty;
                int cut = mime.IndexOf(';');
                if (cut >= 0) mime = mime[..cut];
                mime = mime.Trim();
                bool isPdf = mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                    || mime.Equals("application/x-pdf", StringComparison.OrdinalIgnoreCase);
                string uri = e.Request.Uri;
                // Fetch metadata rides every request Chromium makes: "document" names the
                // top-level frame itself, iframe/frame a view inside someone else's page.
                // A redirect ends the navigation at an address NavigationStarting never
                // saw, so each top-level answer refreshes the recorded main address.
                string dest = e.Request.Headers.Contains("Sec-Fetch-Dest")
                    ? e.Request.Headers.GetHeader("Sec-Fetch-Dest") ?? string.Empty
                    : string.Empty;
                bool mainDocument = dest.Equals("document", StringComparison.OrdinalIgnoreCase);
                if (mainDocument) _mainNavUri = uri;
                if (!isPdf)
                {
                    // v1.19.5: a pdf-shaped address that delivered an ordinary page
                    // (2xx, the top-level answer) gets the engine straight back -
                    // the viewer was never coming.
                    // v1.19.8: an HTML answer - a Cloudflare challenge included -
                    // is a page the reader must see and solve, WHATEVER status code
                    // it carries: Wiley's interstitial answers 403, and the old
                    // 2xx-only stand-down kept the engine blanked through the whole
                    // challenge, invisible and unclickable.
                    bool interstitial = mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                        && (mainDocument || UriEquals(uri, _mainNavUri));
                    if (_browserGuarded && (interstitial
                        || (e.Response.StatusCode is >= 200 and < 300
                            && (mainDocument || UriEquals(uri, _mainNavUri)) && LooksLikePdf(uri))))
                        DropVisualGuard(restore: true);
                    return;
                }
                // v1.19.6: the capture slot is built and assigned BEFORE the hand-off
                // worker starts, so the worker waits on a source that already exists
                // and the bytes the engine is copying land in it.
                // v1.19.7: the dest filter is GONE - the trap that left Wiley,
                // ResearchGate and the viewcontent.cgi routes stranded. Every
                // application/pdf answer is captured WHATEVER frame asked for it:
                // cross-origin redirect chains strip the fetch metadata down to
                // "empty", the <embed>/<object> wrappers report a frame's dest, and
                // both were skipped as if they were not documents at all.
                // v1.19.8: the start gate is gone with it - the header's word alone
                // starts the hand-off, because a pdf answer arriving anywhere is
                // the document the reader meant to open; a worker already waiting
                // is fed, never orphaned.
                TaskCompletionSource<byte[]?> capture;
                if (_earlyHandoff && _pdfCapture is not null)
                {
                    capture = _pdfCapture;
                }
                else
                {
                    capture = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pdfCapture = capture;
                    StartEarlyPdfHandoff(uri);
                }
                Stream? content = await e.Response.GetContentAsync();
                if (content is null) { capture.TrySetResult(null); return; }
                using MemoryStream bytes = new();
                await content.CopyToAsync(bytes);   // read inside the handler, while the stream is live
                capture.TrySetResult(bytes.ToArray());
            }
            catch
            {
                // a capture that stumbles leaves the floor to the later layers
            }
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // A page asking for a popup gets this view instead of a stranded offscreen
            // window the reader can never see.
            e.Handled = true;
            NavigateTo(e.Uri);
        }

        /// <summary>v1.19.10: the riding helper's word. When even the fixed print
        /// cannot take a document - and the button's anchor download is refused -
        /// the extension falls back to a message to the host, and the host answers
        /// with the credentialed fetch and the hand-off every other road uses.
        /// The helper posts a JSON string, so WebMessageAsJson wraps it as a JSON
        /// string literal: the payload is unwrapped before it is parsed, and a
        /// page that posted the object directly parses as-is.</summary>
        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string payload = e.WebMessageAsJson;
                try { payload = System.Text.Json.JsonSerializer.Deserialize<string>(payload) ?? payload; }
                catch { /* already a JSON object - parse as-is */ }
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("type", out System.Text.Json.JsonElement type)
                    || type.GetString() != "avalanche-open-pdf") return;
                string? url = doc.RootElement.TryGetProperty("url", out System.Text.Json.JsonElement u)
                    ? u.GetString() : null;
                if (string.IsNullOrEmpty(url)) return;
                ShowStatus(TryLoc("Str_Web_PdfOpening"));
                _ = FetchAndHandOffAsync(url);
            }
            catch { /* a message that stumbles is never worth a crash */ }
        }

        /// <summary>The extension button's road: the credentialed out-of-process
        /// fetch - the session's own cookies and user agent - and the same
        /// hand-off the automatic layers use, settle and all, so a handed-off
        /// document leaves no tab behind here either. One flight per address,
        /// like every other road; a failure lands the honest blocked word.</summary>
        private async Task FetchAndHandOffAsync(string url)
        {
            try
            {
                if (!_pdfInFlight.Add(url)) { HideStatus(); return; }
                try
                {
                    byte[]? bytes = await FetchBrowserBytesAsync(url);
                    if (bytes != null && HasPdfHeader(bytes))
                    {
                        string target = TempPdfPath(SafePdfName("extension-download.pdf"));
                        await File.WriteAllBytesAsync(target, bytes);
                        SettleAfterHandoff(url);
                        HideStatus();
                        PdfRequested?.Invoke(target);
                        return;
                    }
                    ShowTransientStatus(TryLoc("Str_Web_PdfBlocked"));
                }
                finally { _pdfInFlight.Remove(url); }
            }
            catch { HideStatus(); }
        }

        // ── The sidebar's web-tabs gallery (v1.19.5) ─────────────────────────────────────
        // The window's left rail shows these cards while the pane is up: one per open
        // view, newest first since v1.19.7 and holding its place since v1.19.11 - the
        // captured preview, the page's title and its host - with the view on screen
        // wearing the accent ring. Clicking a card is clicking that tab: the browser
        // navigates there, and the card never jumps because of it.

        /// <summary>The gallery's cards, newest first: a brand-new tab lands on top
        /// (v1.19.7), and since v1.19.11 it STAYS where creation put it - revisiting
        /// a tab refreshes its face, never its position. Capped at a dozen; the
        /// oldest view falls off the end.</summary>
        public ObservableCollection<WebTabCardVm> Tabs { get; } = new();

        private const int MaxWebTabCards = 12;

        /// <summary>The window clicked a gallery card or a strip tab: the browser
        /// switches to that view. The ring moves at once - the click IS the switch,
        /// the navigation only fills the view in - and a click on the view already
        /// on screen does not reload it.</summary>
        public void ActivateTab(string url)
        {
            foreach (WebTabCardVm t in Tabs) t.IsActive = UriEquals(t.Url, url);
            try
            {
                if (UriEquals(Browser.Source?.ToString(), url)) return;   // already there
            }
            catch { /* a source that refuses probing navigates as usual */ }
            NavigateTo(url);
        }

        /// <summary>The window asked the pane to step aside because its last tab
        /// closed. Raised on the UI thread.</summary>
        public event Action? CloseRequested;

        /// <summary>A strip tab's ✕: the card leaves the gallery, and when it was
        /// the view on screen the browser lands on the next newest view - or the
        /// window is asked to put the browser away when that was the last one,
        /// exactly what closing the old single tab did.</summary>
        public void CloseTab(string url)
        {
            WebTabCardVm? card = Tabs.FirstOrDefault(t => UriEquals(t.Url, url));
            if (card is null) return;
            bool wasActive = card.IsActive;
            Tabs.Remove(card);
            if (!wasActive) return;
            if (Tabs.Count == 0)
            {
                CloseRequested?.Invoke();
                return;
            }
            foreach (WebTabCardVm t in Tabs) t.IsActive = false;
            Tabs[0].IsActive = true;
            string next = Tabs[0].Url;
            try
            {
                if (UriEquals(Browser.Source?.ToString(), next)) return;   // already there
            }
            catch { /* a source that refuses probing navigates as usual */ }
            NavigateTo(next);
        }

        /// <summary>The + button and Ctrl+T: a fresh view on the home page, its gallery
        /// card seeded the moment the click lands, and the omnibox holding the caret for
        /// whatever the reader is about to type. When the navigation lands,
        /// UpdateTabCardAsync upgrades the seeded card with the page's own face.</summary>
        public void OpenNewTab()
        {
            string url = HomePage;
            WebTabCardVm? card = Tabs.FirstOrDefault(t => UriEquals(t.Url, url));
            if (card is null)
            {
                card = new WebTabCardVm(url)
                {
                    Title = TryLoc("Str_Web_NewTab") ?? "duckduckgo.com",
                    Host = "duckduckgo.com",
                };
                Tabs.Insert(0, card);   // a brand-new tab still lands on top (v1.19.7)
                while (Tabs.Count > MaxWebTabCards) Tabs.RemoveAt(Tabs.Count - 1);
            }
            // v1.19.11: a revisit activates the card where it lives - the rail keeps
            // creation order, so switching tabs never reshuffles it. Only the ring
            // moves.
            foreach (WebTabCardVm t in Tabs) t.IsActive = UriEquals(t.Url, url);
            NavigateTo(url);
            // The caret goes to the omnibox, deferred once so the click's own focus
            // changes cannot undo it - the same defer OnPaneShown uses.
            Dispatcher.BeginInvoke(
                () => { OmniBox.Clear(); OmniBox.Focus(); },
                System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>Refresh this view's card after a navigation: preview from
        /// CapturePreviewAsync, title from the document, host from the address.
        /// A guarded (about-to-hand-off) document captures nothing, and neither
        /// does a hidden pane - the engine may be suspended.</summary>
        private async Task UpdateTabCardAsync()
        {
            try
            {
                CoreWebView2? core = Browser.CoreWebView2;
                if (core is null || _browserGuarded || !IsVisible) return;
                string url = Browser.Source?.ToString() ?? string.Empty;
                if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? u)) return;
                string title = core.DocumentTitle;
                if (string.IsNullOrWhiteSpace(title)) title = u.Host;
                byte[] png;
                using (MemoryStream ms = new())
                {
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                    png = ms.ToArray();
                }
                ImageSource? thumb = null;
                try
                {
                    BitmapImage img = new();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = new MemoryStream(png);
                    img.EndInit();
                    img.Freeze();
                    thumb = img;
                }
                catch { /* a capture that will not decode leaves the old preview up */ }
                WebTabCardVm? card = Tabs.FirstOrDefault(t => UriEquals(t.Url, url));
                if (card is null)
                {
                    card = new WebTabCardVm(url);
                    Tabs.Insert(0, card);   // a brand-new tab still lands on top (v1.19.7)
                    while (Tabs.Count > MaxWebTabCards) Tabs.RemoveAt(Tabs.Count - 1);
                }
                // v1.19.11: a known card stays where creation put it - only its
                // Title, Host, Thumb and IsActive flags refresh below.
                card.Title = title;
                card.Host = u.Host;
                if (thumb is not null) card.Thumb = thumb;
                foreach (WebTabCardVm t in Tabs) t.IsActive = UriEquals(t.Url, url);
            }
            catch { /* a gallery that stumbles never disturbs the browsing */ }
        }

        // ── Bookmarks (v1.19.11) - the storage and its surface ────────────────────────
        // One JSON file, one collection, two refreshes: the chips' row (hint or
        // list) and the star's fill. Everything else above is clicks.

        /// <summary>The bookmarks, newest first. The chips' ItemsControl binds here.</summary>
        public ObservableCollection<BookmarkVm> Bookmarks { get; } = new();

        private static string BookmarksFile => Path.Combine(AppDataPaths.UserRoot, "bookmarks.json");

        private void LoadBookmarks()
        {
            try
            {
                if (!File.Exists(BookmarksFile)) return;
                List<BookmarkVm>? saved = System.Text.Json.JsonSerializer.Deserialize<List<BookmarkVm>>(
                    File.ReadAllText(BookmarksFile));
                if (saved is null) return;
                foreach (BookmarkVm b in saved)
                {
                    if (b.Url.Length == 0) continue;
                    Bookmarks.Add(b);
                }
            }
            catch { /* a bookmarks file that will not read is not worth a crash */ }
            RefreshBookmarksSurface();
        }

        private void PersistBookmarks()
        {
            try
            {
                Directory.CreateDirectory(AppDataPaths.UserRoot);
                string json = System.Text.Json.JsonSerializer.Serialize(
                    Bookmarks.ToList(),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                string temporary = BookmarksFile + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, BookmarksFile, overwrite: true);
            }
            catch { /* a bookmarks file that will not write is not worth a crash */ }
        }

        private BookmarkVm? FindBookmark(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            return Bookmarks.FirstOrDefault(b => UriEquals(b.Url, url));
        }

        private void OpenBookmarkFlyout(BookmarkVm? editing)
        {
            _bookmarkEditing = editing;
            if (editing is not null)
            {
                WebBookmarkNameBox.Text = editing.Name;
            }
            else
            {
                string title = Browser.CoreWebView2?.DocumentTitle ?? string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                    title = Browser.Source?.Host ?? string.Empty;
                WebBookmarkNameBox.Text = title;
            }
            WebBookmarkPopup.IsOpen = true;
            Dispatcher.BeginInvoke(
                () => { WebBookmarkNameBox.Focus(); WebBookmarkNameBox.SelectAll(); },
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void CommitBookmarkFlyout()
        {
            string name = WebBookmarkNameBox.Text.Trim();
            WebBookmarkPopup.IsOpen = false;
            if (_bookmarkEditing is not null)
            {
                if (name.Length > 0) _bookmarkEditing.Name = name;   // an empty box keeps the old name
                _bookmarkEditing = null;
                PersistBookmarks();
                return;
            }
            string url = Browser.Source?.ToString() ?? string.Empty;
            if (url.Length == 0 || FindBookmark(url) is not null) return;
            Bookmarks.Insert(0, new BookmarkVm
            {
                Name = name.Length > 0 ? name : (Browser.Source?.Host ?? url),
                Url = url,
                Favicon = string.Empty,   // the globe stands in until a favicon earns its keep
            });
            PersistBookmarks();
            RefreshBookmarksSurface();
            RefreshBookmarkButton();
        }

        /// <summary>The star's fill follows the page on screen: filled when the page
        /// is already saved, outlined when it is new. Refreshed on navigation and on
        /// every bookmarks change.</summary>
        private void RefreshBookmarkButton()
        {
            string url = Browser.Source?.ToString() ?? string.Empty;
            WebBookmarkBtn.Content = FindBookmark(url) is not null ? "\uE735" : "\uE728";
        }

        private void RefreshBookmarksSurface()
        {
            bool any = Bookmarks.Count > 0;
            WebBookmarksHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            WebBookmarksList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Small shared helpers ──────────────────────────────────────────────────────────

        private static string SafePdfName(string pathOrUrl)
        {
            string name = Uri.UnescapeDataString(pathOrUrl);
            int cut = name.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) name = name[..cut];
            name = Path.GetFileName(name.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) name = "web.pdf";
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>The temp area the reader's web-opened books live in. v1.19.5
        /// hoisted the literal out of TempPdfPath so the window can recognize a
        /// web-downloaded document for the toolbar's save button.</summary>
        internal static string WebDownloadsDir => Path.Combine(Path.GetTempPath(), "Avalanche", "WebDownloads");

        /// <summary>True when the path is a book the browser downloaded into the
        /// reader's temp area - the web PDF the save button exists for.</summary>
        internal static bool IsWebDownloadsPath(string? path)
            => !string.IsNullOrEmpty(path)
               && path.StartsWith(WebDownloadsDir, StringComparison.OrdinalIgnoreCase);

        private static string TempPdfPath(string name)
        {
            string dir = WebDownloadsDir;
            Directory.CreateDirectory(dir);
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            string target = Path.Combine(dir, name);
            int n = 1;
            while (File.Exists(target)) target = Path.Combine(dir, $"{stem} ({++n}){ext}");
            return target;
        }

        // ── The toolbar light (v1.19.11) ────────────────────────────────────────────
        // The hand-off's word left the status strip for a 24px light in the toolbar
        // row, right after the omnibox: a spinner while a document is being fetched,
        // a green check for two seconds when the reader took it, a red cross for
        // three when the site refused. The strip keeps only the runtime-missing
        // message - with no engine at all there is no light worth dressing the word
        // in - and the row's height never changes for any of them.

        /// <summary>The capture is running: the spinner turns, and the word it used
        /// to speak rides the tooltip. Before the engine exists the strip says it.</summary>
        private void ShowStatus(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!_engineReady)
            {
                WebStatus.Text = text;
                WebStatus.Visibility = Visibility.Visible;
                return;
            }
            ShowActivity(ActivityState.Spinning, text);
        }

        /// <summary>The refusal, as a red cross: three seconds and the light steps
        /// aside on its own. v1.19.7's rule stands in a new shape - no word sticks.</summary>
        private void ShowTransientStatus(string? text)
        {
            if (string.IsNullOrEmpty(text)) { ClearActivity(); return; }
            if (!_engineReady)
            {
                WebStatus.Text = text;
                WebStatus.Visibility = Visibility.Visible;
                return;
            }
            ShowActivity(ActivityState.Failed, text);
            ScheduleActivityTakeDown(TimeSpan.FromSeconds(3));
        }

        /// <summary>The verdict the reader cares about: the green check flashes for
        /// two seconds and the light steps aside. While nothing was showing, the
        /// light stays dark - a check nobody earned is noise.</summary>
        private void HideStatus()
        {
            WebStatus.Visibility = Visibility.Collapsed;
            if (_activity == ActivityState.Hidden) return;
            ShowActivity(ActivityState.Success, null);
            ScheduleActivityTakeDown(TimeSpan.FromSeconds(2));
        }

        private void ShowActivity(ActivityState state, string? tip)
        {
            StopActivityTakeDown();
            if (state == ActivityState.Hidden)
            {
                _activity = ActivityState.Hidden;
                StopSpin();
                WebActivityIndicator.Visibility = Visibility.Collapsed;
                return;
            }
            bool spinning = state == ActivityState.Spinning;
            WebActivityGlyph.Text = spinning ? "\uE72C"
                : state == ActivityState.Success ? "\uE73E" : "\uE711";
            WebActivityGlyph.Foreground = spinning
                ? TryFindResource("MutedTextBrush") as Brush ?? Brushes.Gray
                : state == ActivityState.Success ? Brushes.ForestGreen : Brushes.IndianRed;
            WebActivityIndicator.ToolTip = tip;
            WebActivityIndicator.Visibility = Visibility.Visible;
            _activity = state;
            if (spinning) StartSpin();
            else
            {
                StopSpin();
                WebActivitySpin.Angle = 0;
            }
        }

        private void ClearActivity() => ShowActivity(ActivityState.Hidden, null);

        private void ScheduleActivityTakeDown(TimeSpan delay)
        {
            StopActivityTakeDown();
            _activityTakeDown = new System.Windows.Threading.DispatcherTimer { Interval = delay };
            _activityTakeDown.Tick += (s, _) =>
            {
                if (s is not System.Windows.Threading.DispatcherTimer timer) return;
                timer.Stop();
                _activityTakeDown = null;
                ClearActivity();
            };
            _activityTakeDown.Start();
        }

        private void StopActivityTakeDown()
        {
            _activityTakeDown?.Stop();
            _activityTakeDown = null;
        }

        private void StartSpin()
        {
            if (_spinTimer is not null) return;
            _spinTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _spinTimer.Tick += (_, _) => WebActivitySpin.Angle = (WebActivitySpin.Angle + 15) % 360;
            _spinTimer.Start();
        }

        private void StopSpin()
        {
            _spinTimer?.Stop();
            _spinTimer = null;
        }

        private string? TryLoc(string key) => TryFindResource(key) as string;
    }

    /// <summary>One card in the sidebar's web-tabs gallery (v1.19.5): the page's
    /// captured preview, its title and host, and whether it is the view on
    /// screen. Cards keep their creation position (new ones land on top since
    /// v1.19.11); the active one wears the accent ring.</summary>
    public sealed class WebTabCardVm : INotifyPropertyChanged
    {
        public WebTabCardVm(string url) { Url = url; }

        /// <summary>The page's address - the card's identity and its click target.</summary>
        public string Url { get; }

        private string _title = "";
        /// <summary>The page's own title, or its host until one arrives.</summary>
        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }

        private string _host = "";
        /// <summary>The site the page came from (stands in for a favicon).</summary>
        public string Host { get => _host; set { _host = value; OnPropertyChanged(); } }

        private ImageSource? _thumb;
        /// <summary>The captured preview; null keeps the previous frame.</summary>
        public ImageSource? Thumb { get => _thumb; set { _thumb = value; OnPropertyChanged(); } }

        private bool _isActive;
        /// <summary>The view currently on screen - the accent ring's paint flag.</summary>
        public bool IsActive { get => _isActive; set { _isActive = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }

    /// <summary>One saved page in the browser's bookmarks (v1.19.11): the name the
    /// reader gave it, the address it opens, and the favicon slot the globe
    /// stands in for. Rows live newest first and persist to bookmarks.json in
    /// the app's data root.</summary>
    public sealed class BookmarkVm : INotifyPropertyChanged
    {
        private string _name = "";
        /// <summary>The chip's label - the page's own title until the reader renames it.</summary>
        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }

        /// <summary>The address the chip opens - the bookmark's identity.</summary>
        public string Url { get; set; } = "";

        /// <summary>Reserved for the site's icon; empty keeps the globe glyph.</summary>
        public string Favicon { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }
}
