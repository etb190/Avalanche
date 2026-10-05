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
using Microsoft.Web.WebView2.Wpf;

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
    /// a revisit refreshes a card, never reshuffles the row. v1.19.13:
    /// every tab carries its own view - switching tabs shows one and collapses the
    /// rest instead of re-navigating a shared engine, so a tab keeps its page, its
    /// scroll and its typing until it is closed; the rail and the strip learn to
    /// drag a tab to its place; the omnibox search speaks Google; and the paste
    /// shortcut reaches the page again. v1.19.14: the reader's own shortcut chain
    /// steps aside whenever a page holds the keyboard - Ctrl+A/C/V/X/Z/R and every
    /// chord the chain ever took now do the page's work, exactly like any browser.
    /// The toolbar's save button wears the same white as every toolbar button, the
    /// rail's cards route on the press itself, the ANGLE backend speaks D3D11
    /// WARP, and AdGuard rides along: the Chrome Web Store cannot install into
    /// this engine, so the app fetches the very CRX the store would serve and
    /// loads it through the same extension door the pdf helper uses.
    /// v1.19.16: the omnibox is an address bar that also searches - a link
    /// the reader types or pastes goes where it points, scheme or not, and
    /// only a question with spaces in it stays a search. Everything
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

        private const string HomePage = "https://www.google.com/";

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

        // v1.19.13: one engine environment, one live view per tab. Every card in the
        // gallery owns its own WebView2 sharing the one profile, so switching tabs
        // swaps which view is on screen instead of re-navigating one shared engine -
        // a tab keeps its page, its scroll and its typing until it is closed.
        private CoreWebView2Environment? _env;
        private bool _envBuilding;
        private readonly List<WebView2> _views = new();
        private WebView2? _activeView;
        private bool _extensionLoaded;
        private int _viewGen;   // a newer switch owns the screen; an older build stands down

        // v1.19.14: AdGuard AdBlocker rides with the browser. The Chrome Web Store
        // cannot install into this engine - its installer is bound to full Chrome,
        // so the CRX it sends dies as "Download interrupted" before it becomes an
        // extension - so the app plays the installer: one fetch of the very CRX
        // Google's official update endpoint serves for this extension id, unpacked
        // into the app's data root and loaded through the same
        // AddBrowserExtensionAsync door the little pdf helper rides. A failed fetch
        // is never remembered; the next launch quietly tries again.
        private const string AdGuardExtId = "bgnkhhnnamicmpeenaelnjfhikgbkllg";
        private const string AdGuardCrxUrl =
            "https://update.googleapis.com/service/update2/crx?response=redirect" +
            "&acceptformat=crx2,crx3&x=id%3D" + AdGuardExtId +
            "%26uc&prodversion=131.0.0.0";
        private static string AdGuardExtDir =>
            Path.Combine(AppDataPaths.UserRoot, "WebView2Data", "extensions", "adguard");
        private static readonly System.Net.Http.HttpClient WbHttp = new()
        {
            Timeout = TimeSpan.FromSeconds(120),
        };
        private bool _adGuardLoaded;     // the profile has the extension (or it is on disk)
        private bool _adGuardFetching;   // one fetch at a time

        /// <summary>The view the reader is looking at. Every navigation, capture and
        /// chrome refresh speaks about this one view; background views keep living
        /// their own lives until the reader closes them.</summary>
        private WebView2? Browser => _activeView;

        public WebBrowserControl()
        {
            InitializeComponent();
            // Ctrl+T is the + button's keyboard face (the tooltip says so): the control
            // tunnels the gesture wherever the browser's own surface holds the focus -
            // the omnibox above all.
            // v1.19.14: Ctrl+V no longer rides a synthetic replay. The paste shortcut
            // (and every other chord) died at the window's own tunnel, root-first -
            // the reader's chain answered before the page could hear the key - so
            // the window stands down now whenever a page holds the keyboard, and
            // the chords reach the engine the honest way: as the keyboard's own.
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
            if (_activeView is not null)
            {
                try { _activeView.CoreWebView2?.Resume(); } catch { /* busy or gone: the next show retries */ }
                try
                {
                    // v1.19.5: a reopened pane never sits on Chromium's viewer - if
                    // the last navigation ended on a document, step back to what
                    // offered it.
                    if (LooksLikePdf(_activeView.Source?.ToString())) RetreatFromInlinePdf(_activeView);
                }
                catch { /* a source that refuses probing stays as it is */ }
            }
            // v1.19.13: the first show builds the engine and the first view; a pane
            // whose gallery closed entirely gets its one view back the same way.
            if (_env is null || _activeView is null) _ = EnsureReadyAsync();
            foreach (WebView2 v in _views.ToArray()) DropVisualGuard(v, restore: true);
            _ = EnsureAdGuardAsync();   // v1.19.14: a fetch that failed earlier retries here
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
            foreach (WebView2 v in _views.ToArray())
            {
                try { v.Dispose(); } catch { /* best effort during shutdown */ }
            }
        }

        private async Task SuspendAsync()
        {
            if (_activeDownloads > 0) return;   // a file is being written; suspension can wait
            // v1.19.13: every view sleeps - the pane is gone, not one tab of it.
            foreach (WebView2 v in _views.ToArray())
            {
                try
                {
                    if (v.CoreWebView2 is { } core)
                        await core.TrySuspendAsync();
                }
                catch
                {
                    // Suspension is a courtesy, never a requirement.
                }
            }
        }

        private async Task EnsureReadyAsync()
        {
            if (_env is null)
            {
                if (_envBuilding) return;
                _envBuilding = true;
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
                    // v1.19.14: the ANGLE backend is pinned to D3D11 WARP - the
                    // choice the reader already makes in every other browser
                    // (brave://flags/#use-angle) - the software rasterizer that
                    // trades a little speed for never wearing a GPU driver's
                    // bugs: no black panes, no scrambled canvases, ever.
                    CoreWebView2EnvironmentOptions options = new()
                    {
                        AreBrowserExtensionsEnabled = true,
                        AdditionalBrowserArguments = "--use-angle=warp",
                    };
                    _env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
                }
                catch
                {
                    _env = null;   // the remedy (installing the runtime) can be retried live
                    ShowStatus(TryLoc("Str_Web_RuntimeMissing"));
                    return;
                }
                finally
                {
                    _envBuilding = false;
                }
                HideStatus();
                _engineReady = true;   // the light exists from here on; the strip retires
            }

            // A pane with no view - the very first open, or a + that arrived before
            // the engine - gets one now: the address that was waiting, or home.
            if (_activeView is null)
            {
                string url = _pendingUrl ?? HomePage;
                _pendingUrl = null;
                await CreateTabAsync(url);
            }
            SetChromeEnabled(true);
            RefreshBookmarkButton();
            _ = EnsureAdGuardAsync();   // v1.19.14: the blocker rides along, never in the way
        }

        /// <summary>The one wiring every view wears: the PDF hand-off's watchtowers,
        /// the history keys, the title that names the tab, and the riding helper's
        /// word. The extension loads once per profile; everything else is per view.
        /// v1.19.7's viewer-toolbar ban stands on every view's own settings.</summary>
        private void WireView(WebView2 view)
        {
            CoreWebView2 core = view.CoreWebView2!;
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
            view.NavigationStarting += OnNavigationStarting;
            view.NavigationCompleted += OnNavigationCompleted;
            core.DocumentTitleChanged += (_, _) => HandleTitleChanged(view);
            // v1.19.10: the riding helper - a two-kilobyte extension that wears
            // the "open in Avalanche" button on the viewer itself - is loaded
            // once per profile; loading it is a courtesy, never a requirement.
            if (!_extensionLoaded)
            {
                _extensionLoaded = true;
                try
                {
                    string extPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        "Resources", "WebExtensions", "avalanche-pdf");
                    if (Directory.Exists(extPath))
                        _ = core.Profile.AddBrowserExtensionAsync(extPath);
                }
                catch { /* extension loading is a courtesy */ }
                // v1.19.14: AdGuard rides the same door the moment it is on disk -
                // the first launch fetches it, every launch after finds it waiting.
                TryLoadAdGuardOnce(core);
            }
            core.WebMessageReceived += OnWebMessageReceived;
        }

        // ── Navigation ────────────────────────────────────────────────────────────────────

        private void WebBackBtn_Click(object sender, RoutedEventArgs e)
        {
            try { Browser?.CoreWebView2?.GoBack(); } catch { /* nothing to retrace yet */ }
        }

        private void WebForwardBtn_Click(object sender, RoutedEventArgs e)
        {
            try { Browser?.CoreWebView2?.GoForward(); } catch { /* nothing to retrace yet */ }
        }

        private void WebRefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_activeView?.CoreWebView2 is null) _ = EnsureReadyAsync();
                else _activeView.Reload();
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

        // v1.19.15: the Edit/Delete menu wears the house face everywhere. The themed
        // ContextMenu and MenuItem styles are window-scoped, and an implicit style does
        // not always reach a menu declared inside a UserControl's template - a menu that
        // opens in the framework's own chrome is the small white box the reader saw
        // flash, its rows wearing the default gutter the house template fills. On the
        // opening event, whatever the ambient lookup failed to dress is dressed by hand
        // from the host window's own resources - a no-op where the ambient won.
        private void BookmarkChip_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not Button { ContextMenu: ContextMenu menu } chip) return;
            Window? host = Window.GetWindow(chip);
            if (host is null) return;
            if (menu.Style is null && host.TryFindResource(typeof(ContextMenu)) is Style menuFace)
                menu.Style = menuFace;
            foreach (MenuItem item in menu.Items.OfType<MenuItem>())
                if (item.Style is null && host.TryFindResource(typeof(MenuItem)) is Style itemFace)
                    item.Style = itemFace;
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
            string url = Browser?.Source?.ToString() ?? string.Empty;
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

        // v1.19.15: the omnibox's first click selects the whole address - the browser
        // convention. A second single click falls through to the caret's own business
        // (place it where the reader pointed), and a double-click selects everything
        // again instead of the one word WPF's TextBox would take.
        private void OmniBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBox box) return;
            if (!box.IsKeyboardFocusWithin)
            {
                // First click on a resting omnibox: take the focus and select all.
                // Handled, so the press's own caret placement cannot collapse the
                // selection the moment it lands.
                e.Handled = true;
                box.Focus();
                box.SelectAll();
            }
            else if (e.ClickCount >= 2)
            {
                // A press that arrives as a double-click selects the whole thing again.
                e.Handled = true;
                box.SelectAll();
            }
            // Every other press while focused: exactly what it does today.
        }

        // The omnibox: Enter commits (address when it looks like one, search when it does
        // not), Escape hands the text back to the page and returns to the web.
        private void OmniBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                NavigateTo(ParseInput(OmniBox.Text));
                try { _activeView?.Focus(); } catch { /* a view gone already */ }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SyncOmniFromBrowser();
                try { _activeView?.Focus(); } catch { /* a view gone already */ }
            }
        }

        private void OmniBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            OmniHint.Visibility = string.IsNullOrEmpty(OmniBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>The omnibox is an address bar that also searches, not a search
        /// box that also takes addresses: an absolute http(s)/ftp/file address goes
        /// as-is; a bare host the reader plainly meant as a place - www., localhost,
        /// an ipv4, a dotted name whose last label reads like a tld, port and path
        /// included - gains https:// and goes; anything else is a search - the
        /// reading-browser default. v1.19.16: a link goes where it points, scheme
        /// or not, and a pasted address keeps working even when the copy ran it
        /// across lines; a question with spaces in it stays a search.</summary>
        internal static string ParseInput(string raw)
        {
            string text = raw.Trim();
            if (text.Length == 0) return HomePage;
            if (LooksLikeAddressStart(text))
            {
                // a pasted address often arrives with the copy's line wrapping still
                // in it: a scheme'd paste wraps anywhere, so every break comes out;
                // a www. paste only loses its line breaks - "www. what is this"
                // keeps the space that makes it a search.
                text = text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? StripLineBreaks(text)
                    : StripWhitespace(text);
            }
            if (Uri.TryCreate(text, UriKind.Absolute, out Uri? abs)
                && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps
                    || abs.Scheme == Uri.UriSchemeFtp || abs.Scheme == Uri.UriSchemeFile))
                return abs.ToString();
            if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate("https://" + text, UriKind.Absolute, out Uri? www))
                return www.ToString();
            if (LooksLikeBareHost(text, out string bare)
                && Uri.TryCreate("https://" + bare, UriKind.Absolute, out Uri? host))
                return host.ToString();
            return "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
        }

        /// <summary>The reader has already said "address" when the text opens with a
        /// scheme or a www. host - only then is the whitespace inside it the copy's
        /// wrapping rather than the words of a search.</summary>
        private static bool LooksLikeAddressStart(string text)
            => text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

        private static string StripWhitespace(string text)
            => new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

        private static string StripLineBreaks(string text)
            => text.Replace("\r", "").Replace("\n", "").Replace("\t", "");

        /// <summary>A place, not a question: no whitespace and no @ anywhere (an
        /// email is a search, not a host), and the part before the first path,
        /// query or fragment mark reads as a host - localhost, an ipv4, or a
        /// dotted name whose last label is letters, the way every browser reads
        /// example.com, arxiv.org and sub.domain.co.uk. The host it approves is
        /// handed back, so the navigation goes to the same name the check read.</summary>
        private static bool LooksLikeBareHost(string text, out string candidate)
        {
            candidate = text;
            if (text.Length == 0 || text.IndexOf('@') >= 0 || text.Any(char.IsWhiteSpace))
                return false;
            string host = text;
            string rest = "";
            int cut = host.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0) { rest = host[cut..]; host = host[..cut]; }
            if (host.EndsWith('.')) host = host[..^1];   // a dns name may sign off with a dot
            if (host.Length == 0) return false;
            int colon = host.LastIndexOf(':');
            if (colon >= 0)
            {
                string port = host[(colon + 1)..];
                if (port.Length == 0 || port.Length > 5 || port.Any(c => !char.IsAsciiDigit(c)))
                    return false;
            }
            string name = colon >= 0 ? host[..colon] : host;
            if (name.Length == 0) return false;
            bool place;
            if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                place = true;
            else
            {
                string[] labels = name.Split('.');
                place = labels.Length >= 2 && labels.All(l => l.Length > 0)
                    && (labels.Length == 4
                        && labels.All(l => l.All(char.IsAsciiDigit))
                        && labels.All(l => int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out int o) && o <= 255)
                        || labels[^1].Length >= 2 && labels[^1].All(char.IsAsciiLetter));
            }
            if (place) candidate = host + rest;   // the trimmed name, port and path intact
            return place;
        }

        private void NavigateTo(string url)
        {
            if (_activeView?.CoreWebView2 is null)
            {
                _pendingUrl = url;   // the first page waits for the engine, never the reverse
                _ = EnsureReadyAsync();
                return;
            }
            try { _activeView.CoreWebView2.Navigate(url); }
            catch { /* a navigation that throws is the next one's problem */ }
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (sender is not WebView2 v) return;
            if (!ReferenceEquals(v, _activeView))
            {
                _ = UpdateTabCardAsync(v, background: true);   // a background tab still updates its own face
                return;
            }
            SyncOmniFromBrowser();
            RefreshBookmarkButton();   // v1.19.11: the star follows the page on screen
            HandleTitleChanged(v);
            _lastPageUrl = v.Source?.ToString();
            if (e.IsSuccess)
            {
                _navCompleted = true;   // a straggling early hand-off can owe the fallback now
                _ = CatchInlinePdfAsync(v);
            }
            else
            {
                DropVisualGuard(v, restore: true);   // a dead navigation never strands a blanked engine
                HideStatus();   // v1.19.7: nor a "Fetching..." strip
            }
            _ = UpdateTabCardAsync(v);   // the sidebar gallery's preview for this view
        }

        /// <summary>The one PDF the browser is allowed to show is none. A link no rule can
        /// see coming - /getpdf?id=..., a redirect that ends in a document - starts
        /// rendering in Chromium's own viewer; the moment the page finishes, the document
        /// is pulled back out with the browser's own credentials - the bytes the engine
        /// itself received, then a fetch from inside the page, then the credentialed
        /// out-of-process fetch - checked, and handed to the reader while the browser
        /// steps back to the page that offered it. Nothing is remembered about a failure:
        /// a hand-off only guards the race it is running.</summary>
        private async Task CatchInlinePdfAsync(WebView2 v)
        {
            bool handed = false;   // the document reached the reader
            try
            {
                CoreWebView2? core = v.CoreWebView2;
                if (core is null) return;
                string type = await core.ExecuteScriptAsync("document.contentType");
                string plain = type.Trim('"');
                string url = v.Source?.ToString() ?? string.Empty;
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
                    DropVisualGuard(v, restore: true);   // an ordinary page: the viewer was never coming
                    return;
                }
                if (url.Length == 0 || !_pdfInFlight.Add(url)) return;
                try
                {
                    ShowStatus(TryLoc("Str_Web_PdfOpening"));
                    byte[]? bytes = await CapturePdfFromBrowserAsync(url, v);
                    handed = await HandPdfToReaderAsync(url, bytes, v);
                    // v1.19.8: the retreat is the hand-off's own last step now -
                    // SettleAfterHandoff walks the view back BEFORE the pane hides,
                    // never across the suspension the hide asks for.
                }
                finally
                {
                    _pdfInFlight.Remove(url);   // the outcome is forgotten; only the race is guarded
                    DropVisualGuard(v, restore: true);   // the engine paints again whatever stayed behind
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
                DropVisualGuard(v, restore: true);
                HideStatus();
            }
        }

        private void RetreatFromInlinePdf(WebView2 v)
        {
            try
            {
                CoreWebView2? core = v.CoreWebView2;
                if (core is null) return;
                if (core.CanGoBack) core.GoBack();
                else core.Navigate(HomePage);
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
        private void ArmVisualGuard(WebView2 v)
        {
            ShowStatus(TryLoc("Str_Web_PdfOpening"));
            if (_browserGuarded) return;
            _browserGuarded = true;
            try { v.Visibility = Visibility.Hidden; } catch { /* a view gone already */ }
        }

        private void DropVisualGuard(WebView2 v, bool restore)
        {
            if (!_browserGuarded) return;
            _browserGuarded = false;
            if (restore)
            {
                try
                {
                    // Only the view on screen gets its paint back; a tab the reader
                    // already left stays collapsed where the tab logic put it.
                    if (ReferenceEquals(v, _activeView)) v.Visibility = Visibility.Visible;
                }
                catch { /* a view gone already */ }
            }
        }

        /// <summary>The response headers just said the MAIN document is a PDF: the
        /// viewer must never paint, and the engine's own bytes are already copying
        /// (OnWebResourceResponseReceived). Wait for the capture, check the header,
        /// hand the reader the document; on success the browser steps back to the
        /// page that offered the link once the navigation has somewhere to go back
        /// to. A failure stands the guard down and leaves the NavigationCompleted
        /// fallback its three sources.</summary>
        private async void StartEarlyPdfHandoff(string url, WebView2 v)
        {
            if (_earlyHandoff || url.Length == 0) return;
            _earlyHandoff = true;
            bool handed = false;   // the reader accepted the document
            try
            {
                if (!_pdfInFlight.Add(url)) return;
                ArmVisualGuard(v);   // blanks the engine even when the address never looked like a PDF
                byte[]? bytes = await WaitPdfCaptureAsync(TimeSpan.FromSeconds(20));
                if (bytes != null && HasPdfHeader(bytes))
                {
                    // v1.19.8: no delayed retreat across the pane's suspension -
                    // the hand-off itself settles the browser while the engine is
                    // still awake: guard down, view back, no pdf tab left behind.
                    handed = await HandPdfToReaderAsync(url, bytes, v);   // the pane steps aside; the reader tab opens
                }
                else
                {
                    // v1.19.6: a failed early hand-off owes the completed-navigation
                    // fallback its turn - immediately when the navigation already
                    // landed, otherwise the moment it does (OnNavigationCompleted
                    // runs it for a navigation still in flight).
                    _pdfInFlight.Remove(url);
                    DropVisualGuard(v, restore: true);
                    HideStatus();
                    if (_navCompleted) _ = CatchInlinePdfAsync(v);
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
        /// <summary>The tab's title: the page's own when it has one, otherwise the
        /// address host. v1.19.13: the title names the card the VIEW belongs to -
        /// every view names its own tab, and only the view on screen raises the
        /// window's event.</summary>
        private void HandleTitleChanged(WebView2 v)
        {
            CoreWebView2? core = v.CoreWebView2;
            if (core is null) return;
            string title = core.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title)) title = v.Source?.Host ?? string.Empty;
            if (title.Length == 0) return;
            try
            {
                WebTabCardVm? card = CardFor(v);
                if (card is not null) card.Title = title;
            }
            catch { /* a gallery hiccup never disturbs the title event */ }
            if (ReferenceEquals(v, _activeView)) TitleChanged?.Invoke(title);
        }

        private void SyncOmniFromBrowser()
        {
            if (OmniBox.IsKeyboardFocused) return;   // never fight the reader's typing
            OmniBox.Text = Browser?.Source?.ToString() ?? string.Empty;
            OmniBox.CaretIndex = OmniBox.Text.Length;
        }

        private void RefreshHistoryButtons()
        {
            CoreWebView2? core = Browser?.CoreWebView2;
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
            // v1.19.13: the watchtowers belong to the view on screen. A background
            // tab navigating itself (a redirect, a refresh) touches none of the
            // capture state - the reader's own navigation owns it.
            if (sender is not WebView2 v || !ReferenceEquals(v, _activeView)) return;
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
            if (LooksLikePdf(e.Uri)) ArmVisualGuard(v);
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
        private async Task<byte[]?> CapturePdfFromBrowserAsync(string url, WebView2 v)
        {
            byte[]? captured = await WaitPdfCaptureAsync(TimeSpan.FromSeconds(20));
            if (captured != null && HasPdfHeader(captured)) return captured;
            byte[]? scripted = await FetchViaPageScriptAsync(v);
            if (scripted != null && HasPdfHeader(scripted)) return scripted;
            byte[]? fetched = await FetchBrowserBytesAsync(url, v);
            if (fetched != null && HasPdfHeader(fetched)) return fetched;
            byte[]? printed = await PrintRenderedPdfAsync(v);
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
        private async Task<byte[]?> PrintRenderedPdfAsync(WebView2 v)
        {
            try
            {
                CoreWebView2? core = v.CoreWebView2;
                if (core is null) return null;
                string url = v.Source?.ToString() ?? string.Empty;
                // The viewer's outer frame lies about its contentType; the
                // address and the capture do not. A capture slot exists only
                // when an application/pdf answer arrived this navigation, so a
                // viewcontent.cgi route whose address never looks like a pdf
                // still reaches the print.
                bool onViewer = url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                    || LooksLikePdf(url)
                    || _pdfCapture is not null;
                if (!onViewer) return null;
                DropVisualGuard(v, restore: true);   // the print needs the engine alive, not blanked
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

        private async Task<byte[]?> FetchViaPageScriptAsync(WebView2 v)
        {
            try
            {
                CoreWebView2? core = v.CoreWebView2;
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
        private async Task<bool> HandPdfToReaderAsync(string url, byte[]? bytes, WebView2 v)
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
                SettleAfterHandoff(url, v);
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
        /// down and the view that carried the document steps back to the page
        /// that offered the link (home when nothing stands behind). v1.19.13: the
        /// tab itself STAYS - a tab is a live view now, and nothing the reader had
        /// open is ever thrown away by a hand-off; its card follows the retreat on
        /// the navigation that lands, so no about:blank shell is left dressed as a
        /// tab and a reopened browser lands on a working page.</summary>
        private void SettleAfterHandoff(string url, WebView2 v)
        {
            try { DropVisualGuard(v, restore: true); } catch { /* a paint flag only */ }
            try
            {
                CoreWebView2? core = v.CoreWebView2;
                if (core is null) return;
                if (core.CanGoBack) core.GoBack();
                else core.Navigate(HomePage);
            }
            catch { /* a settle that stumbles never blocks the hand-off */ }
        }

        /// <summary>v1.19.12: every pdf download attempt owes the view its exit, not
        /// only the ones that succeed. The navigation Chromium turned into a download
        /// leaves the engine standing on a blank provisional page when it stands on
        /// the download's own address; the success path has settled since v1.19.8,
        /// and the refused and interrupted attempts settle here now - the reader is
        /// left on the page that offered the document, never on an about:blank shell
        /// dressed as a tab.</summary>
        private void SettleAfterDownloadAttempt(string dlUrl, WebView2 v)
        {
            try
            {
                string view = v.Source?.ToString() ?? string.Empty;
                if (view.Length > 0 && (UriEquals(view, dlUrl) || LooksLikePdf(view)))
                    SettleAfterHandoff(view, v);
            }
            catch { /* a settle that stumbles never blocks the hand-off */ }
        }

        /// <summary>The fetch, with the session's own credentials: the cookies the WebView2
        /// engine earned (sign-ins included, HttpOnly ones too), the engine's real user
        /// agent, the page that offered the link as referrer, and honest compression.
        /// Sites that answered the old anonymous fetch with an error page answer this one
        /// the way they answer the user's own browser.</summary>
        private async Task<byte[]> FetchBrowserBytesAsync(string url, WebView2 v)
        {
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                                       | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
            };
            CoreWebView2? core = v.CoreWebView2;
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
            // v1.19.13: a download belongs to the view that started it, wherever
            // the reader happens to be looking now - its settle finds that view.
            WebView2? view = ViewOfCore(sender as CoreWebView2);
            if (view is null) return;
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
                        SettleAfterDownloadAttempt(dlUrl, view);
                        PdfRequested?.Invoke(target);
                    }
                    else if (op.State == CoreWebView2DownloadState.Completed)
                    {
                        try { File.Delete(target); } catch { /* temp litter is harmless */ }
                        // v1.19.12: a refused document settles the view too - the
                        // navigation that became this download still left the engine
                        // standing on a blank provisional page, and a red cross over
                        // an about:blank shell is not much of an answer.
                        SettleAfterDownloadAttempt(dlUrl, view);
                        ShowTransientStatus(TryLoc("Str_Web_PdfNotPdf"));   // v1.19.7: the word takes itself down
                    }
                    else
                    {
                        HideStatus();   // interrupted: the status line steps aside
                        // v1.19.12: the interrupted attempt settles as well; the
                        // view that carried the download goes home instead of
                        // freezing on the blank it was left holding.
                        SettleAfterDownloadAttempt(dlUrl, view);
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
                // v1.19.13: the capture serves the view on screen. A background tab
                // that navigates itself into a document still owns its downloads -
                // but the watchtowers, the guard and the early hand-off belong to
                // the reader's own navigation.
                WebView2? view = ViewOfCore(sender as CoreWebView2);
                if (view is null || !ReferenceEquals(view, _activeView)) return;
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
                        DropVisualGuard(view, restore: true);
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
                    StartEarlyPdfHandoff(uri, view);
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
            // window the reader can never see. A blank shell is the exception
            // (v1.19.12): download tricks and scripting scaffolds open about:blank
            // first and write into it after - routing THIS view there only blanked
            // the page the reader was on, and nothing ever arrived in it. The shell
            // is swallowed; the view stays where it was.
            e.Handled = true;
            WebView2? v = ViewOfCore(sender as CoreWebView2);
            if (v is null) return;
            string popup = e.Uri ?? string.Empty;
            if (popup.Length == 0 || popup.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return;
            try { v.CoreWebView2?.Navigate(popup); }
            catch { /* a navigation that throws is the next one's problem */ }
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
                string? alt = doc.RootElement.TryGetProperty("alt", out System.Text.Json.JsonElement altEl)
                    ? altEl.GetString() : null;
                WebView2? v = ViewOfCore(sender as CoreWebView2);
                if (v is null) return;
                ShowStatus(TryLoc("Str_Web_PdfOpening"));
                _ = FetchAndHandOffAsync(url, alt, v);
            }
            catch { /* a message that stumbles is never worth a crash */ }
        }

        /// <summary>The extension button's road: the credentialed out-of-process
        /// fetch - the session's own cookies and user agent - and the same
        /// hand-off the automatic layers use, settle and all, so a handed-off
        /// document leaves no tab behind here either. One flight per address,
        /// like every other road. A wrapper page hands the helper two
        /// candidates: the embedded document's address first, the wrapper's own
        /// address second - each gets one fetch, and the first real PDF bytes
        /// win. A failure lands the honest blocked word, and success is the one
        /// place the green check lives (v1.19.12): the click's own receipt.</summary>
        private async Task FetchAndHandOffAsync(string url, string? alt, WebView2 v)
        {
            try
            {
                if (!_pdfInFlight.Add(url)) { HideStatus(); return; }
                try
                {
                    string handed = url;
                    byte[]? bytes = await TryFetchPdfBytesAsync(url, v);
                    if ((bytes is null || !HasPdfHeader(bytes))
                        && !string.IsNullOrEmpty(alt) && !UriEquals(alt, url))
                    {
                        byte[]? second = await TryFetchPdfBytesAsync(alt, v);   // the wrapper's own address takes its turn
                        if (second != null && HasPdfHeader(second)) { bytes = second; handed = alt; }
                    }
                    if (bytes != null && HasPdfHeader(bytes))
                    {
                        string target = TempPdfPath(SafePdfName("extension-download.pdf"));
                        await File.WriteAllBytesAsync(target, bytes);
                        SettleAfterHandoff(handed, v);
                        CelebrateHandoff();
                        PdfRequested?.Invoke(target);
                        return;
                    }
                    ShowTransientStatus(TryLoc("Str_Web_PdfBlocked"));
                }
                finally { _pdfInFlight.Remove(url); }
            }
            catch { HideStatus(); }
        }

        /// <summary>The credentialed fetch with the sharp edges filed off: a site
        /// that answers with a wall, a reset or an unsupported scheme returns
        /// null - a verdict the caller can speak - instead of an exception that
        /// would cut the second candidate off from its turn.</summary>
        private async Task<byte[]?> TryFetchPdfBytesAsync(string url, WebView2 v)
        {
            try { return await FetchBrowserBytesAsync(url, v); }
            catch { return null; }
        }

        // ── The sidebar's web-tabs gallery (v1.19.5; live views since v1.19.13) ────────
        // The window's left rail shows these cards while the pane is up: one per open
        // tab, newest first and holding its place - the captured preview, the page's
        // title and its host - with the view on screen wearing the accent ring.
        // v1.19.13: a card IS a tab now, and a tab is a live WebView2 of its own.
        // Clicking a card shows that view exactly as it was left - nothing navigates,
        // nothing reloads - and closing it disposes its view. The rail and the strip
        // are one list wearing two faces, and a drag on either (MainWindow's drag
        // dialect) reorders both.

        /// <summary>The gallery's cards, newest first: a brand-new tab lands on top
        /// and STAYS where the reader drags it or creation put it. Capped at a
        /// dozen; the oldest view falls off the end.</summary>
        public ObservableCollection<WebTabCardVm> Tabs { get; } = new();

        private const int MaxWebTabCards = 12;

        /// <summary>The window clicked a gallery card or a strip tab: the browser
        /// switches views. v1.19.13: switching is a SHOW, not a navigation - the
        /// view comes to the screen exactly as it was left, scroll and typing
        /// included, and a click on the view already on screen changes nothing.</summary>
        public void ActivateTab(WebTabCardVm card)
        {
            try
            {
                if (card.View is { } v && ReferenceEquals(v, _activeView))
                {
                    foreach (WebTabCardVm t in Tabs) t.IsActive = ReferenceEquals(t, card);
                    return;   // already there
                }
            }
            catch { /* a view that refuses probing swaps as usual */ }
            _ = ShowViewAsync(card);
        }

        /// <summary>The window asked the pane to step aside because its last tab
        /// closed. Raised on the UI thread.</summary>
        public event Action? CloseRequested;

        /// <summary>A strip tab's ✕: the card leaves the gallery, and when it was
        /// the view on screen the browser lands on the next newest view - or the
        /// window is asked to put the browser away when that was the last one,
        /// exactly what closing the old single tab did.</summary>
        public void CloseTab(WebTabCardVm card)
        {
            if (!Tabs.Contains(card)) return;
            bool wasActive = card.IsActive;
            Tabs.Remove(card);
            // v1.19.13: the tab's view dies with it - the engine behind it is
            // disposed and its memory goes back to Windows.
            if (card.View is { } v)
            {
                if (ReferenceEquals(v, _activeView)) _activeView = null;
                _views.Remove(v);
                try { BrowserHost.Children.Remove(v); } catch { /* a view gone already */ }
                try { v.Dispose(); } catch { /* a view gone already is a fine answer */ }
            }
            if (!wasActive || _activeView is not null) return;
            if (Tabs.Count == 0)
            {
                CloseRequested?.Invoke();
                return;
            }
            _ = ShowViewAsync(Tabs[0]);
        }

        /// <summary>The + button and Ctrl+T: a fresh view on the home page, its gallery
        /// card seeded the moment the click lands, and the omnibox holding the caret for
        /// whatever the reader is about to type. When the navigation lands,
        /// UpdateTabCardAsync upgrades the seeded card with the page's own face.</summary>
        public void OpenNewTab()
        {
            if (_activeView is null || _env is null)
            {
                _ = EnsureReadyAsync();   // the first open owns the first tab
            }
            else
            {
                _ = CreateTabAsync(HomePage);
            }
            // The caret goes to the omnibox, deferred once so the click's own focus
            // changes cannot undo it - the same defer OnPaneShown uses.
            Dispatcher.BeginInvoke(
                () => { OmniBox.Clear(); OmniBox.Focus(); },
                System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>Refresh a view's card after a navigation. The card IS the tab
        /// now (v1.19.13): it follows its own view's address, title, host and
        /// preview, and a navigation never spawns another card. A background view
        /// updates its face only - no preview (the swap may be mid-flight) and no
        /// ring. A guarded (about-to-hand-off) document captures nothing, and
        /// neither does a hidden pane - the engine may be suspended.</summary>
        private async Task UpdateTabCardAsync(WebView2 v, bool background = false)
        {
            try
            {
                CoreWebView2? core = v.CoreWebView2;
                if (core is null || !IsVisible) return;
                if (!background && _browserGuarded) return;
                string url = v.Source?.ToString() ?? string.Empty;
                if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? u)) return;
                string title = core.DocumentTitle;
                if (string.IsNullOrWhiteSpace(title)) title = u.Host;
                WebTabCardVm? card = CardFor(v);
                if (card is null)
                {
                    card = new WebTabCardVm(url);
                    Tabs.Insert(0, card);   // a view without a card earns one at the top
                    TrimWebTabCards(card);
                }
                card.Url = url;
                card.Title = title;
                card.Host = u.Host;
                if (background) return;
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
                if (thumb is not null) card.Thumb = thumb;
                foreach (WebTabCardVm t in Tabs) t.IsActive = ReferenceEquals(t.View, v);
            }
            catch { /* a gallery that stumbles never disturbs the browsing */ }
        }

        // ── The view per tab (v1.19.13) ────────────────────────────────────────────────────
        // A tab is a live WebView2: one view per card, every view sharing the one
        // environment and profile. Switching tabs shows one view and collapses the
        // rest - nothing navigates, nothing reloads - and closing a tab disposes
        // its view. The capture pipeline stays the active view's own, exactly as
        // v1.19.8-12 built it; background views simply live.

        /// <summary>The card a view belongs to - the tab's identity is its view.</summary>
        private WebTabCardVm? CardFor(WebView2 v)
        {
            foreach (WebTabCardVm t in Tabs)
                if (ReferenceEquals(t.View, v)) return t;
            return null;
        }

        /// <summary>The view a core event came from - the watchtowers ride each
        /// view's core, and every answer must find its way home.</summary>
        private WebView2? ViewOfCore(CoreWebView2? core)
        {
            if (core is null) return null;
            foreach (WebView2 v in _views)
            {
                try { if (ReferenceEquals(v.CoreWebView2, core)) return v; }
                catch { /* a view gone already */ }
            }
            return null;
        }

        private static string HostOf(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? u.Host : string.Empty;

        /// <summary>The dozen cap: the oldest card that is not the view on screen
        /// falls off the end, its view disposed with it.</summary>
        private void TrimWebTabCards(WebTabCardVm keep)
        {
            while (Tabs.Count > MaxWebTabCards)
            {
                WebTabCardVm? oldest = Tabs.LastOrDefault(t => !t.IsActive && !ReferenceEquals(t, keep));
                if (oldest is null) break;
                CloseTab(oldest);
            }
        }

        /// <summary>A new tab: its card seeded at once (top of the rail), its view
        /// built and walked to its first address. The engine must exist - callers
        /// without one go through EnsureReadyAsync, which lands here after.</summary>
        private async Task CreateTabAsync(string url)
        {
            if (_env is null)
            {
                _pendingUrl = url;
                _ = EnsureReadyAsync();
                return;
            }
            WebTabCardVm card = new(url)
            {
                Title = TryLoc("Str_Web_NewTab") ?? "google.com",
                Host = HostOf(url),
                IsActive = true,
            };
            foreach (WebTabCardVm t in Tabs) t.IsActive = false;
            Tabs.Insert(0, card);   // a brand-new tab lands on top
            TrimWebTabCards(card);
            WebView2 view = await BuildViewAsync(card);
            await ShowViewAsync(card, view, url);
        }

        /// <summary>One view for one tab: built transparent over the themed card,
        /// collapsed until shown, wired once, and kept in the host for its whole
        /// life - a tab's page survives every switch, which is the point.</summary>
        private async Task<WebView2> BuildViewAsync(WebTabCardVm card)
        {
            WebView2 view = new()
            {
                // A transparent engine over the themed card: no white flash while
                // the page loads, and the blank state belongs to the theme.
                DefaultBackgroundColor = System.Drawing.Color.Transparent,
                Visibility = Visibility.Collapsed,
            };
            card.View = view;
            _views.Add(view);
            BrowserHost.Children.Add(view);
            if (_env is not null)
            {
                await view.EnsureCoreWebView2Async(_env);
                WireView(view);
            }
            return view;
        }

        /// <summary>Puts one view on screen and collapses the rest - the whole act
        /// of switching tabs now. Nothing navigates: the view is exactly the page
        /// it was, scroll, typing and all, until the reader closes it.</summary>
        private async Task ShowViewAsync(WebTabCardVm card, WebView2? view = null, string? navigate = null)
        {
            int gen = ++_viewGen;
            view ??= card.View;
            if (view is null)
            {
                if (_env is null)
                {
                    _pendingUrl = card.Url;
                    _ = EnsureReadyAsync();
                    return;
                }
                view = await BuildViewAsync(card);
                if (gen != _viewGen) return;   // a newer switch owns the screen now
            }
            foreach (WebView2 v in _views.ToArray())
            {
                try { v.Visibility = ReferenceEquals(v, view) ? Visibility.Visible : Visibility.Collapsed; }
                catch { /* a view gone already */ }
            }
            _activeView = view;
            foreach (WebTabCardVm t in Tabs) t.IsActive = ReferenceEquals(t.View, view);
            try
            {
                view.CoreWebView2?.Resume();   // a view hidden since the pane's last show wakes here
                if (navigate is not null && view.CoreWebView2 is not null
                    && !UriEquals(view.Source?.ToString(), navigate))
                    view.CoreWebView2.Navigate(navigate);
                view.Focus();
            }
            catch { /* a first paint that stumbles is the page's own problem */ }
            SyncOmniFromBrowser();
            RefreshHistoryButtons();
            RefreshBookmarkButton();
            HandleTitleChanged(view);
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
                string title = Browser?.CoreWebView2?.DocumentTitle ?? string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                    title = Browser?.Source?.Host ?? string.Empty;
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
            string url = Browser?.Source?.ToString() ?? string.Empty;
            if (url.Length == 0 || FindBookmark(url) is not null) return;
            Bookmarks.Insert(0, new BookmarkVm
            {
                Name = name.Length > 0 ? name : (Browser?.Source?.Host ?? url),
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
            string url = Browser?.Source?.ToString() ?? string.Empty;
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

        /// <summary>Silence, not a verdict (v1.19.12): the light steps aside with
        /// nothing said. The green check this method used to flash fired on every
        /// road that ended well - automatic hand-offs, finished navigations,
        /// cleanup paths - and a check nobody asked for reads as noise. The check
        /// is the extension button's alone now: CelebrateHandoff shows it, and
        /// this method only clears the stage.</summary>
        private void HideStatus()
        {
            WebStatus.Visibility = Visibility.Collapsed;
            ClearActivity();
        }

        /// <summary>The extension button's success, and nobody else's: the green
        /// check flashes for two seconds and the light steps aside. Every other
        /// road ends in HideStatus - silence is the shared verdict, the check is
        /// the click's own receipt.</summary>
        private void CelebrateHandoff()
        {
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

        // ── AdGuard (v1.19.14) ──────────────────────────────────────────────────────
        // The store's own installer cannot speak to this engine, so the app plays
        // the installer: one fetch of the CRX Google's update endpoint serves for
        // the extension's id, the CRX3 envelope peeled off (four bytes of magic,
        // a version, a signed header length, then the zip itself), the zip spread
        // into the data root, and the folder handed to the profile exactly like
        // the helper's. Nothing is remembered about a failure - the next launch
        // fetches again, and a download that never lands changes nothing.

        /// <summary>Load the on-disk AdGuard folder into the profile once per run.</summary>
        private void TryLoadAdGuardOnce(CoreWebView2 core)
        {
            if (_adGuardLoaded || !Directory.Exists(AdGuardExtDir)) return;
            _adGuardLoaded = true;
            try { _ = core.Profile.AddBrowserExtensionAsync(AdGuardExtDir); }
            catch { /* a courtesy, like every extension load */ }
        }

        /// <summary>The blocker on disk: fetch it once if it is missing, then let
        /// the profile take it. Fire-and-forget on every path; browsing never waits.</summary>
        private async Task EnsureAdGuardAsync()
        {
            if (_adGuardLoaded) return;
            if (Directory.Exists(AdGuardExtDir))
            {
                if (_activeView?.CoreWebView2 is { } existing) TryLoadAdGuardOnce(existing);
                return;
            }
            if (_adGuardFetching) return;
            _adGuardFetching = true;
            try
            {
                byte[] crx = await WbHttp.GetByteArrayAsync(AdGuardCrxUrl);
                UnpackAdGuardCrx(crx);
                if (_activeView?.CoreWebView2 is { } core) TryLoadAdGuardOnce(core);
            }
            catch
            {
                // a fetch that fails is a launch that browses without the
                // blocker; nothing is remembered, so the next show tries again.
            }
            finally
            {
                _adGuardFetching = false;
            }
        }

        /// <summary>CRX3 in, extension folder out: the envelope is twelve bytes of
        /// fixed header plus a proto header whose length the third dword carries;
        /// everything after it is the zip the store itself ships.</summary>
        private static void UnpackAdGuardCrx(byte[] crx)
        {
            if (crx.Length < 16 || crx[0] != (byte)'C' || crx[1] != (byte)'r'
                || crx[2] != (byte)'2' || crx[3] != (byte)'4')
                throw new InvalidDataException("Not a CRX payload");
            uint version = BitConverter.ToUInt32(crx, 4);
            uint headerLen = BitConverter.ToUInt32(crx, 8);
            if (version < 3 || headerLen == 0 || crx.Length <= 12 + (long)headerLen)
                throw new InvalidDataException("Malformed CRX envelope");
            string root = Path.Combine(AppDataPaths.UserRoot, "WebView2Data", "extensions");
            Directory.CreateDirectory(root);
            string tmp = Path.Combine(root,
                "adguard.unpack." + Path.GetRandomFileName().Replace(".", ""));
            try
            {
                using MemoryStream zipStream = new(crx, (int)(12 + headerLen),
                    crx.Length - (int)(12 + headerLen), writable: false);
                using System.IO.Compression.ZipArchive archive =
                    new(zipStream, System.IO.Compression.ZipArchiveMode.Read);
                foreach (System.IO.Compression.ZipArchiveEntry entry in archive.Entries)
                {
                    string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (rel.Length == 0 || rel.EndsWith(Path.DirectorySeparatorChar)) continue;
                    if (rel.Split(Path.DirectorySeparatorChar).Contains("..")) continue;   // hygiene
                    string target = Path.Combine(tmp, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using Stream src = entry.Open();
                    using FileStream dst = File.Create(target);
                    src.CopyTo(dst);
                }
                if (!File.Exists(Path.Combine(tmp, "manifest.json")))
                    throw new InvalidDataException("Extension archive has no manifest");
                if (Directory.Exists(AdGuardExtDir))
                    Directory.Delete(AdGuardExtDir, recursive: true);
                Directory.Move(tmp, AdGuardExtDir);
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
                catch { /* a stray temp folder outlives us only until the next fetch */ }
            }
        }

        // ── The page's keyboard (v1.19.14) ──────────────────────────────────────────
        // The reader's shortcut chain lives at the window's tunnel, and a tunnel runs
        // root-first: when a web page holds the keyboard, the chain used to answer
        // every chord before the page could - Ctrl+A selected the reader's list,
        // Ctrl+C copied the reader's text, Ctrl+V pasted the reader's clipboard,
        // Ctrl+R rotated the reader's pages, and the rest died the same quiet death.
        // Now the control speaks for its views: when the keyboard's true owner is a
        // page - WPF's own focus, or the win32 focus inside any live view - the
        // window's chain steps aside and every chord reaches the engine as the
        // keyboard delivered it, exactly like any browser the reader has used.

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        /// <summary>Does a live page own the keyboard right now? WPF's own focus
        /// answer first - a view as the focused element - then the win32 truth:
        /// the hwnd the thread's input currently serves must live inside one of
        /// the engine's own views.</summary>
        public bool PageOwnsKeyboard()
        {
            if (Keyboard.FocusedElement is WebView2) return true;
            try
            {
                IntPtr focus = GetFocus();
                if (focus == IntPtr.Zero) return false;
                foreach (WebView2 v in _views)
                {
                    IntPtr h = ((System.Windows.Interop.IWin32Window)v).Handle;
                    if (h != IntPtr.Zero && (h == focus || IsChild(h, focus))) return true;
                }
            }
            catch { /* a view gone already is no keyboard owner */ }
            return false;
        }

        private string? TryLoc(string key) => TryFindResource(key) as string;
    }

    /// <summary>One card in the sidebar's web-tabs gallery (v1.19.5): the page's
    /// captured preview, its title and host, and whether it is the view on
    /// screen. Cards keep their creation position (new ones land on top since
    /// v1.19.11); the active one wears the accent ring.</summary>
    public sealed class WebTabCardVm : INotifyPropertyChanged
    {
        public WebTabCardVm(string url) { _url = url; }

        private string _url;
        /// <summary>The view's current address. v1.19.13: the card's identity is its
        /// view, not this string - the address follows the page as the reader
        /// browses, and the card stays the same tab.</summary>
        public string Url { get => _url; set { _url = value; OnPropertyChanged(); } }

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

        /// <summary>The live view this tab owns (v1.19.13). One per card, for the
        /// tab's whole life: switching tabs shows it, closing the tab disposes it,
        /// and nothing in between asks it to navigate anywhere.</summary>
        public WebView2? View { get; set; }

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
