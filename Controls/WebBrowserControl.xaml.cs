using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
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
    /// pdf-shaped links are recognized on sight (suffix, trailing path parameters and
    /// /pdf/ routes), anything that slips through and starts rendering in Chromium's own
    /// viewer is pulled back out when the page finishes, every fetch rides the session's
    /// own cookies and user agent and is checked for a real PDF header before the
    /// reader hears of it, and no download is ever interrupted by a suspend. Everything
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

        // One honest fetch attempt per URL: a link that will not yield a PDF is shown as
        // the page it is, never re-intercepted in a loop. URLs that do deliver are taken
        // back out, so a later click works again.
        private readonly HashSet<string> _pdfTried = new(StringComparer.Ordinal);

        // Downloads the engine is still writing: suspension waits until the last one
        // lands, because TrySuspendAsync mid-download is how a "damaged" PDF is born.
        private int _activeDownloads;

        public WebBrowserControl()
        {
            InitializeComponent();
            // A transparent engine over the themed card: no white flash while the page
            // loads, and the blank state belongs to the theme instead of Chromium.
            Browser.DefaultBackgroundColor = System.Drawing.Color.Transparent;
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
                env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await Browser.EnsureCoreWebView2Async(env);
            }
            catch
            {
                _initStarted = false;   // the remedy (installing the runtime) can be retried live
                ShowStatus(TryLoc("Str_Web_RuntimeMissing"));
                return;
            }

            HideStatus();
            CoreWebView2 core = Browser.CoreWebView2!;
            core.DownloadStarting += OnDownloadStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.HistoryChanged += (_, _) => RefreshHistoryButtons();
            Browser.NavigationStarting += OnNavigationStarting;
            Browser.NavigationCompleted += OnNavigationCompleted;
            core.DocumentTitleChanged += (_, _) => RaiseTitleChanged();
            SetChromeEnabled(true);
            NavigateTo(_pendingUrl ?? HomePage);
            _pendingUrl = null;
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

        private void DialChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string url }) NavigateTo(url);
        }

        private void WebOpenPdfBtn_Click(object sender, RoutedEventArgs e) => _ = OpenCurrentPageAsPdfAsync();

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
            RaiseTitleChanged();
            _lastPageUrl = Browser.Source?.ToString();
            if (e.IsSuccess) _ = CatchInlinePdfAsync();
        }

        /// <summary>The one PDF the browser is allowed to show is none. A link no rule can
        /// see coming - /getpdf?id=..., a redirect that ends in a document - starts
        /// rendering in Chromium's own viewer; the moment the page finishes, the document
        /// is pulled back out: fetched with the session's credentials, checked, and handed
        /// to the reader while the browser steps back to the page that offered it. A URL
        /// the fetch already failed for is simply left behind - the reader never lives
        /// inside a PDF viewer of its own.</summary>
        private async Task CatchInlinePdfAsync()
        {
            try
            {
                CoreWebView2? core = Browser.CoreWebView2;
                if (core is null) return;
                string type = await core.ExecuteScriptAsync("document.contentType");
                if (!type.Trim('"').Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                    return;
                string url = Browser.Source?.ToString() ?? string.Empty;
                if (url.Length == 0 || _pdfTried.Contains(url))
                {
                    ShowStatus(TryLoc("Str_Web_PdfBlocked"));
                    RetreatFromInlinePdf();
                    return;
                }
                await OpenRemotePdfAsync(url);
                RetreatFromInlinePdf();   // rest on the page that offered the PDF, never on a viewer
            }
            catch
            {
                // a catch-all that stumbles leaves the browser exactly as it is
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

        /// <summary>The tab's title: the page's own when it has one, otherwise the
        /// address host. Empty stays silent - the tab keeps whatever it wore.</summary>
        private void RaiseTitleChanged()
        {
            CoreWebView2? core = Browser.CoreWebView2;
            if (core is null) return;
            string title = core.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title)) title = Browser.Source?.Host ?? string.Empty;
            if (title.Length > 0) TitleChanged?.Invoke(title);
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
                = WebHomeBtn.IsEnabled = WebOpenPdfBtn.IsEnabled = on;
            if (on) RefreshHistoryButtons();
        }

        // ── The PDF hand-off ──────────────────────────────────────────────────────────────
        // A PDF is never rendered by the browser. A pdf-shaped link cancels its navigation
        // and is fetched with the session's own credentials into the reader's temp area; a
        // document that slips through and starts rendering in Chromium's own viewer is
        // pulled back out the moment it loads; every fetch is checked for a real PDF
        // header before the reader ever hears of it; a PDF download suppresses Edge's
        // flyout, lands in the same place, and is never cut short by a suspend; the
        // "open in Avalanche" button prints the live page straight to a temp PDF. Every
        // path ends at PdfRequested, which the window turns into an ordinary reader tab.

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (LooksLikePdf(e.Uri) && !_pdfTried.Contains(e.Uri))
            {
                e.Cancel = true;
                _ = OpenRemotePdfAsync(e.Uri);
            }
        }

        /// <summary>A URL worth intercepting on sight: the path ends in .pdf even when a
        /// path parameter trails it (.pdf;jsessionid=... is a classic), or the route itself
        /// is a /pdf/ one - arXiv, ACM, PubMed Central and most publishers hand the paper
        /// over at such addresses. A false positive costs nothing: the fetch below checks
        /// the bytes and shows the page as-is when it turns out not to be a PDF.</summary>
        internal static bool LooksLikePdf(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
            string path = u.AbsolutePath;
            int semi = path.IndexOf(';');
            if (semi >= 0) path = path[..semi];
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return true;
            string lower = path.ToLowerInvariant();
            return lower.Contains("/pdf/") || lower.EndsWith("/pdf");
        }

        private async Task OpenRemotePdfAsync(string url)
        {
            _pdfTried.Add(url);   // one honest attempt; the outcome decides what a repeat click does
            ShowStatus(TryLoc("Str_Web_PdfOpening"));
            try
            {
                string target = TempPdfPath(SafePdfName(new Uri(url).AbsolutePath));
                byte[] bytes = await FetchBrowserBytesAsync(url);
                if (!HasPdfHeader(bytes))
                {
                    // The site answered with a page, not a PDF (a sign-in wall, a landing,
                    // an error dressed as a download). Showing that page is what any real
                    // browser does; feeding the bytes to the reader is how "damaged" PDFs
                    // were born.
                    ShowStatus(TryLoc("Str_Web_PdfNotPdf"));
                    NavigateTo(url);
                    return;
                }
                await File.WriteAllBytesAsync(target, bytes);
                _pdfTried.Remove(url);   // a link that delivers stays clickable
                HideStatus();
                PdfRequested?.Invoke(target);
            }
            catch
            {
                // The navigation was cancelled, so the page the reader was on is untouched;
                // say why the PDF never came instead of failing in silence.
                ShowStatus(TryLoc("Str_Web_PdfBlocked"));
            }
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
                        catch { /* one uncooperative cookie skips itself, the rest ride along */ }
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
            bool isPdf = mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                || suggested.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            if (!isPdf) return;   // ordinary downloads keep the browser's own handling

            _activeDownloads++;   // every download, PDF or not: suspension must not strand it
            bool counted = true;
            if (!isPdf)
            {
                // ordinary downloads keep the browser's own handling; suspension still waits
                // for them, so the engine is never paused under a file it is writing
                e.DownloadOperation.StateChanged += (s, _) =>
                {
                    if (s is CoreWebView2DownloadOperation op
                        && op.State != CoreWebView2DownloadState.InProgress)
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (counted) { counted = false; _activeDownloads--; }
                        });
                };
                return;
            }
            e.Handled = true;   // no Edge download flyout for a file the reader is about to eat
            string target = TempPdfPath(SafePdfName(suggested.Length > 0 ? suggested : "download.pdf"));
            e.ResultFilePath = target;
            ShowStatus(TryLoc("Str_Web_PdfOpening"));
            e.DownloadOperation.StateChanged += (s, _) =>
            {
                if (s is not CoreWebView2DownloadOperation op) return;
                if (op.State == CoreWebView2DownloadState.Completed)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (counted) { counted = false; _activeDownloads--; }
                        HideStatus();
                        if (HasPdfHeaderFile(target))
                            PdfRequested?.Invoke(target);
                        else
                        {
                            try { File.Delete(target); } catch { /* temp litter is harmless */ }
                            ShowStatus(TryLoc("Str_Web_PdfNotPdf"));
                        }
                    });
                }
                else if (op.State != CoreWebView2DownloadState.InProgress)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (counted) { counted = false; _activeDownloads--; }
                        HideStatus();
                    });
                }
            };
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // A page asking for a popup gets this view instead of a stranded offscreen
            // window the reader can never see.
            e.Handled = true;
            NavigateTo(e.Uri);
        }

        private async Task OpenCurrentPageAsPdfAsync()
        {
            if (Browser.CoreWebView2 is null) return;
            ShowStatus(TryLoc("Str_Web_PdfOpening"));
            try
            {
                string target = TempPdfPath(
                    "page-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".pdf");
                await Browser.CoreWebView2.PrintToPdfAsync(target, null);
                PdfRequested?.Invoke(target);
            }
            catch
            {
                HideStatus();
            }
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

        private static string TempPdfPath(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "Avalanche", "WebDownloads");
            Directory.CreateDirectory(dir);
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            string target = Path.Combine(dir, name);
            int n = 1;
            while (File.Exists(target)) target = Path.Combine(dir, $"{stem} ({++n}){ext}");
            return target;
        }

        private void ShowStatus(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            WebStatus.Text = text;
            WebStatus.Visibility = Visibility.Visible;
        }

        private void HideStatus() => WebStatus.Visibility = Visibility.Collapsed;

        private string? TryLoc(string key) => TryFindResource(key) as string;
    }
}
