using System;
using System.Globalization;
using System.IO;
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
    /// every PDF the page offers - a clicked link or a finished download - into the reader
    /// instead of the browser's own viewer. Everything is lazy: no WebView2 process exists
    /// until the pane is first shown, and TrySuspendAsync hands the engine's memory and GPU
    /// surfaces back to Windows whenever the pane hides again.
    /// </summary>
    public partial class WebBrowserControl : UserControl
    {
        /// <summary>A local PDF is ready for the reader. Raised on the UI thread.</summary>
        public event Action<string>? PdfRequested;

        private const string HomePage = "https://duckduckgo.com/";

        private bool _initStarted;
        private string? _pendingUrl;

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
        // A PDF is never rendered by the browser. A clicked .pdf link cancels its navigation
        // and downloads into the reader's temp area; a PDF download suppresses Edge's flyout
        // and lands in the same place; the "open in Avalanche" button prints the live page
        // straight to a temp PDF. Every path ends at PdfRequested, which the window turns
        // into an ordinary reader tab.

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (LooksLikePdf(e.Uri))
            {
                e.Cancel = true;
                _ = OpenRemotePdfAsync(e.Uri);
            }
        }

        /// <summary>Direct .pdf URLs are the rule from the brief; arXiv's /pdf/&lt;id&gt; links
        /// carry no suffix, so the hand-off recognizes the host too - a clicked arXiv result
        /// opens in the reader exactly like a Gutenberg PDF.</summary>
        internal static bool LooksLikePdf(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
            if (u.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return true;
            string host = u.Host.ToLowerInvariant();
            if (host == "arxiv.org" || host.EndsWith(".arxiv.org"))
                return u.AbsolutePath.StartsWith("/pdf/", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private async Task OpenRemotePdfAsync(string url)
        {
            ShowStatus(TryLoc("Str_Web_PdfOpening"));
            try
            {
                string name = SafePdfName(new Uri(url).AbsolutePath);
                string target = TempPdfPath(name);
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromMinutes(3);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Avalanche");
                byte[] bytes = await http.GetByteArrayAsync(url);
                File.WriteAllBytes(target, bytes);
                PdfRequested?.Invoke(target);
            }
            catch
            {
                HideStatus();   // the page stays; the reader never sees a half download
            }
        }

        private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            string mime = e.DownloadOperation.MimeType ?? string.Empty;
            string suggested = e.ResultFilePath ?? string.Empty;
            bool isPdf = mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                || suggested.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            if (!isPdf) return;   // ordinary downloads keep the browser's own handling

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
                        HideStatus();
                        PdfRequested?.Invoke(target);
                    });
                }
                else if (op.State == CoreWebView2DownloadState.Interrupted)
                {
                    Dispatcher.BeginInvoke(HideStatus);
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
