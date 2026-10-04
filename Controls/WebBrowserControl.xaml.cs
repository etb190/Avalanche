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
    /// nothing is ever cancelled, so the request a site sees is always Chromium's own -
    /// downloads stream through the engine straight into the reader's temp area, and a
    /// document that still reaches Chromium's viewer is pulled back out with the bytes
    /// the engine itself received, then a fetch from inside the page, then the
    /// credentialed out-of-process fetch as the last resort, every candidate checked
    /// for a real PDF header before the reader hears of it. A failed hand-off is never
    /// remembered, so a blocked link can always be retried, and no download is ever
    /// interrupted by a suspend. Everything
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
            core.WebResourceResponseReceived += OnWebResourceResponseReceived;
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
        /// is pulled back out with the browser's own credentials - the bytes the engine
        /// itself received, then a fetch from inside the page, then the credentialed
        /// out-of-process fetch - checked, and handed to the reader while the browser
        /// steps back to the page that offered it. Nothing is remembered about a failure:
        /// a hand-off only guards the race it is running.</summary>
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
                if (url.Length == 0 || !_pdfInFlight.Add(url)) return;
                try
                {
                    ShowStatus(TryLoc("Str_Web_PdfOpening"));
                    byte[]? bytes = await CapturePdfFromBrowserAsync(url);
                    await HandPdfToReaderAsync(url, bytes);
                    RetreatFromInlinePdf();   // rest on the page that offered the PDF, never on a viewer
                }
                finally
                {
                    _pdfInFlight.Remove(url);   // the outcome is forgotten; only the race is guarded
                }
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
            // fresh.
            _pdfCapture = null;
        }

        /// <summary>The document's bytes, from the sources closest to Chromium inward:
        /// first the response the engine itself received for this very document (no
        /// second request exists for a CDN or an anti-bot wall to distrust), then a fetch
        /// running inside the page - the session's own cookies, tokens and TLS
        /// handshake - and only then the credentialed out-of-process fetch that shares
        /// the engine's cookies and user agent. The first source that yields real PDF
        /// bytes wins; null means every one of them failed.</summary>
        private async Task<byte[]?> CapturePdfFromBrowserAsync(string url)
        {
            byte[]? captured = await WaitPdfCaptureAsync(TimeSpan.FromSeconds(20));
            if (captured != null && HasPdfHeader(captured)) return captured;
            byte[]? scripted = await FetchViaPageScriptAsync();
            if (scripted != null && HasPdfHeader(scripted)) return scripted;
            byte[]? fetched = await FetchBrowserBytesAsync(url);
            if (fetched != null && HasPdfHeader(fetched)) return fetched;
            return null;
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
        /// last time, written into the reader's temp area, and handed over; anything else
        /// speaks through the status line instead of feeding the reader damaged goods.</summary>
        private async Task HandPdfToReaderAsync(string url, byte[]? bytes)
        {
            if (bytes is null || !HasPdfHeader(bytes))
            {
                ShowStatus(TryLoc("Str_Web_PdfBlocked"));
                return;
            }
            try
            {
                string path = Uri.TryCreate(url, UriKind.Absolute, out Uri? u)
                    ? u.AbsolutePath : string.Empty;
                string target = TempPdfPath(SafePdfName(path));
                await File.WriteAllBytesAsync(target, bytes);
                HideStatus();
                PdfRequested?.Invoke(target);
            }
            catch
            {
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
                        PdfRequested?.Invoke(target);
                    }
                    else if (op.State == CoreWebView2DownloadState.Completed)
                    {
                        try { File.Delete(target); } catch { /* temp litter is harmless */ }
                        ShowStatus(TryLoc("Str_Web_PdfNotPdf"));
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
                if (!mime.Trim().Equals("application/pdf", StringComparison.OrdinalIgnoreCase)) return;
                Stream? content = await e.Response.GetContentAsync();
                if (content is null) return;
                TaskCompletionSource<byte[]?> capture =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                _pdfCapture = capture;
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

        /// <summary>The fallback that captures what the reader is looking at right now:
        /// when the page is itself a document, its real bytes are handed over through the
        /// same three sources as the automatic hand-off, and when it is not, the live
        /// page prints to a temp PDF as always.</summary>
        private async Task OpenCurrentPageAsPdfAsync()
        {
            CoreWebView2? core = Browser.CoreWebView2;
            if (core is null) return;
            ShowStatus(TryLoc("Str_Web_PdfOpening"));
            try
            {
                string url = Browser.Source?.ToString() ?? string.Empty;
                if (url.Length > 0)
                {
                    try
                    {
                        string type = await core.ExecuteScriptAsync("document.contentType");
                        if (type.Trim('"').Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_pdfInFlight.Add(url))
                            {
                                try
                                {
                                    byte[]? bytes = await CapturePdfFromBrowserAsync(url);
                                    await HandPdfToReaderAsync(url, bytes);
                                }
                                finally { _pdfInFlight.Remove(url); }
                            }
                            return;   // a document page never prints; in flight means it is already coming
                        }
                    }
                    catch { /* a page that refuses probing prints as usual */ }
                }
                string target = TempPdfPath(
                    "page-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".pdf");
                await core.PrintToPdfAsync(target, null);
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
