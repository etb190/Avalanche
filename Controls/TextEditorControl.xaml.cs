using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Avalanche.Services;

namespace Avalanche.Controls
{
    // The text editor's host (v1.19.64): a ribbon of real writing controls and one
    // WebView2 running the embedded editor document (Controls/TextEditorDocument.cs).
    // The split is deliberate - the ribbon is WPF and speaks theme brushes like every
    // other bar in the app, while the page itself is a plain HTML document where real
    // pagination, behind-text images, hyperlinks and footnotes are first-class citizens
    // instead of fights. The two halves talk over WebView2 messages: the ribbon posts
    // commands (bold, font, size, link, footnote, image), the page posts facts back
    // (what the caret is wearing, how many pages exist, what to save, links to open).
    // Chromium never ships in the payload - the Evergreen runtime the browser pane
    // already rides wakes here too, lazily, and suspends when the pane hides.
    public partial class TextEditorControl : UserControl
    {
        private CoreWebView2Environment? _env;
        private WebView2? _web;
        private bool _envBuilding;
        private bool _pageReady;
        private string? _pendingLoadHtml;
        private int _pageCount;
        private int _activePage = 1;

        // ── Tabs (v1.19.69): the PDF editor's own model, not a lookalike ────
        // The reader asked for the SAME tabs the PDF editor and browser wear
        // and meant the machinery too: every visual decision below is a
        // NOTIFYING FLAG the strip's template triggers read - never a property
        // painted onto a code-built card. One session, one truth, and the
        // ItemsControl repaints itself.
        private sealed class EditorTab : INotifyPropertyChanged
        {
            public string Html = "";
            private readonly string _untitled;

            public EditorTab(string title, string html, string untitled)
            {
                _untitled = untitled;
                _title = title;
                Html = html;
                _tabLabel = _title.Length > 0 ? _title : _untitled;
                _tabTip = _tabLabel;
            }

            private string _title;
            public string Title
            {
                get => _title;
                set
                {
                    if (_title == value) return;
                    _title = value;
                    TabLabel = _title.Length > 0 ? _title : _untitled;
                }
            }

            private string _tabLabel;
            /// <summary>The strip's face: the document's name, or Untitled -
            /// repainted with the title, never by a full rebuild.</summary>
            public string TabLabel
            {
                get => _tabLabel;
                private set { _tabLabel = value; TabTip = value; Fire(); }
            }

            private string _tabTip;
            public string TabTip
            {
                get => _tabTip;
                private set { _tabTip = value; Fire(); }
            }

            private bool _isActive;
            /// <summary>The tab on screen - the accent ring's paint flag.</summary>
            public bool IsActive
            {
                get => _isActive;
                set { if (_isActive == value) return; _isActive = value; Fire(); }
            }

            private bool _isStripVisible = true;
            /// <summary>Windowed into the strip (ApplyTabWindow); the rest live
            /// in the chevron. Collapsed, not removed - UniformGrid does not
            /// count a collapsed child, so the survivors fill the band.</summary>
            public bool IsStripVisible
            {
                get => _isStripVisible;
                set { if (_isStripVisible == value) return; _isStripVisible = value; Fire(); }
            }

            private bool _isFirst;
            public bool IsFirst
            {
                get => _isFirst;
                set { if (_isFirst == value) return; _isFirst = value; Fire(); }
            }

            private bool _isLast;
            public bool IsLast
            {
                get => _isLast;
                set { if (_isLast == value) return; _isLast = value; Fire(); }
            }

            private bool _useRetroTabChrome;
            /// <summary>The classic theme's own tab chrome; modern themes
            /// resolve every retro token to nothing.</summary>
            public bool UseRetroTabChrome
            {
                get => _useRetroTabChrome;
                set { if (_useRetroTabChrome == value) return; _useRetroTabChrome = value; Fire(); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void Fire([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        }

        private sealed class SessionData
        {
            public List<SessionTab>? Tabs { get; set; }
            public int Active { get; set; }
        }

        private sealed class SessionTab
        {
            public string? Title { get; set; }
            public string? Html { get; set; }
        }

        private readonly ObservableCollection<EditorTab> _tabs = [];

        // One raster per tab (v1.19.71): the last pictures each document drew
        // of itself. A tab switch paints the cache the instant the tab lands,
        // so the rail never flashes down to bare labels while the fresh raster
        // is 900ms away - the pictures simply stay where the reader left them.
        private readonly System.Collections.Generic.Dictionary<EditorTab, string[]> _thumbCache = [];
        private int _activeTab;
        private int? _pendingSwitchTo;   // a tab click waiting for the page's dump
        private int _worldSeq;           // which loaded world the page is showing
        private bool _syncingLists;      // a dial highlighting itself must not apply
        private string _untitledLabel = "Untitled";
        private string _lastAppliedSize = "12";   // what the size input reverts to

        // ── The strip's windowing: as many tabs as fit at the floor width stay
        // in the band, the rest live in the chevron - the reader's own cap,
        // ported with the constants that make it honest.
        private const double TabFloorWidth = 120;
        private const double TabCeilingWidth = 240;
        private const double TabChevronWidth = 26;
        private const double TabNewWidth = 28;   // the + shares the band
        private int _tabWindow;                  // leftmost tab currently in the strip
        private bool _inTabResize;               // the SizeChanged reentrancy guard
        private EditorTab? _tabDragSession;
        private Point _tabDragStart;
        private double _tabGrabDX;
        private bool _tabDragging;

        /// <summary>The page count moved: the sidebar's page rail rebuilds.</summary>
        public event Action<int>? PageCountChanged;

        /// <summary>The caret moved to another page (1-based): the rail highlights it.</summary>
        public event Action<int>? ActivePageChanged;

        /// <summary>A link the reader Ctrl+clicked (or a page asked to open) - the host
        /// decides where it goes (system browser).</summary>
        public event Action<string>? LinkOpenRequested;

        /// <summary>The ribbon's browser switch: the host swaps interfaces.</summary>
        public event Action? BrowserRequested;

        /// <summary>The ribbon's PDF editor switch: the host swaps interfaces.</summary>
        public event Action? PdfRequested;

        /// <summary>The page rasterized itself for the sidebar's rail: one PNG
        /// data URL per page (empty where a page refused), the way the PDF list
        /// wears one thumbnail per page and the browser's gallery wears one
        /// captured preview per tab. null means the world changed and the
        /// rail's cache is stale.</summary>
        public event Action<string[]?>? ThumbsChanged;

        public int PageCount => _pageCount;

        private static readonly string[] FontChoices =
        {
            "Segoe UI", "Arial", "Calibri", "Cambria", "Consolas", "Courier New",
            "Georgia", "Impact", "Palatino Linotype", "Tahoma", "Times New Roman",
            "Trebuchet MS", "Verdana",
        };

        private static readonly string[] SizeChoices =
        {
            "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "32", "36", "48", "72",
        };

        public TextEditorControl()
        {
            InitializeComponent();
            FontList.ItemsSource = FontChoices;
            SizeList.ItemsSource = SizeChoices;
            FontPopup.Closed += (_, _) => FontBtn.IsChecked = false;
            _untitledLabel = Loc("Str_Editor_Untitled");
            LoadEditorTabs();
            InitTabStrip();
            RebuildTabStrip();
            // Ctrl+Z / Ctrl+Y must answer from the ribbon too: while a WPF
            // control held the keyboard the page never saw the keys, and the
            // reader read the silence as "nothing to undo" (v1.19.68). The
            // page keeps its own path for when the caret stands in it.
            PreviewKeyDown += EditorPane_PreviewKeyDown;
        }

        private void EditorPane_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Alt) || !mods.HasFlag(ModifierKeys.Control)) return;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k == Key.Z)
            {
                e.Handled = true;
                Post(new { cmd = mods.HasFlag(ModifierKeys.Shift) ? "redo" : "undo" });
            }
            else if (k == Key.Y)
            {
                e.Handled = true;
                Post(new { cmd = "redo" });
            }
        }

        private string Loc(string key)
            => TryFindResource(key) as string ?? key;

        // ── Pane lifecycle (the browser pane's own bargain, shared) ─────────────

        /// <summary>The pane just showed: wake the engine, or build it on first open.</summary>
        public void OnPaneShown()
        {
            try { _web?.CoreWebView2?.Resume(); } catch { /* busy or gone: the next show retries */ }
            _ = EnsureReadyAsync();
        }

        /// <summary>The pane just hid: suspend the engine so an idle editor costs
        /// nothing but a hwnd. The page's own state (caret, content) survives both
        /// ways - suspension is a courtesy, never a reset.</summary>
        public void OnPaneHidden()
        {
            _ = SuspendAsync();
        }

        /// <summary>The window is going away: stop the editor's browser process cleanly.</summary>
        public void ShutdownForExit()
        {
            try { _web?.Dispose(); } catch { /* best effort during shutdown */ }
            _web = null;
        }

        private async Task SuspendAsync()
        {
            try
            {
                if (_web?.CoreWebView2 is { } core)
                    await core.TrySuspendAsync();
            }
            catch
            {
                // Suspension is a courtesy, never a requirement.
            }
        }

        // ── Engine ───────────────────────────────────────────────────────────────

        private async Task EnsureReadyAsync()
        {
            if (_pageReady || _envBuilding) return;
            _envBuilding = true;
            try
            {
                if (_env is null)
                {
                    string dataDir = Path.Combine(AppDataPaths.UserRoot, "WebView2EditorData");
                    Directory.CreateDirectory(dataDir);
                    // The same WARP pin the browser pane wears: the software
                    // rasterizer that never wears a GPU driver's bugs, and an
                    // editor has even less need of a GPU than a browser does.
                    var options = new CoreWebView2EnvironmentOptions
                    {
                        AdditionalBrowserArguments = "--use-angle=warp",
                    };
                    _env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
                }
                if (_web is null)
                {
                    _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
                    WebHost.Children.Add(_web);
                    _web.WebMessageReceived += OnMessageReceived;
                    await _web.EnsureCoreWebView2Async(_env);
                    var core = _web.CoreWebView2;
                    core.Settings.AreDefaultContextMenusEnabled = false;
                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.IsZoomControlEnabled = false;
                    core.Settings.IsStatusBarEnabled = false;
                    core.NavigationStarting += (_, e) =>
                    {
                        // The editor lives on its embedded document and nowhere
                        // else: the initial data: navigation rides, everything
                        // else (a stray link click, a form) stays put.
                        string uri = e.Uri ?? string.Empty;
                        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                            && !uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                            e.Cancel = true;
                    };
                    core.NewWindowRequested += (_, e) =>
                    {
                        // A link that opens a window is a link the reader asked
                        // to leave the page - hand it to the host.
                        e.Handled = true;
                        if (!string.IsNullOrEmpty(e.Uri)) LinkOpenRequested?.Invoke(e.Uri);
                    };
                    _pendingLoadHtml ??= ActiveTabHtml();
                    _web.NavigateToString(TextEditorDocument.Html);
                }
            }
            catch
            {
                _env = null;
                try { _web?.Dispose(); } catch { /* a half-built host is let go */ }
                _web = null;
                EngineStatus.Text = Loc("Str_Editor_RuntimeMissing");
                EngineVeil.Visibility = Visibility.Visible;
            }
            finally
            {
                _envBuilding = false;
            }
        }

        // ── Messages: page -> ribbon ─────────────────────────────────────────────

        private void OnMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // The page may post a JS object or a JSON.stringify'd string;
                // WebMessageAsJson is the message "converted to a JSON string",
                // so a string message arrives QUOTED and parses to a String
                // root. Unwrap that one extra layer before reading - posting
                // style is the page's business, both are welcome here.
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    using var inner = JsonDocument.Parse(root.GetString() ?? "{}");
                    HandlePageMessage(inner.RootElement);
                }
                else
                {
                    HandlePageMessage(root);
                }
            }
            catch
            {
                // A malformed message never takes the editor down.
            }
        }

        private void HandlePageMessage(JsonElement root)
        {
            switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
            {
                case "ready":
                    _pageReady = true;
                    Post(new
                    {
                        cmd = "i18n",
                        apply = Loc("Str_Editor_Apply"),
                        remove = Loc("Str_Editor_Remove"),
                        linkUrl = Loc("Str_Editor_LinkUrl"),
                    });
                    ThumbsChanged?.Invoke(CachedThumbs());   // the tab's own last raster paints now; a fresh one follows
                    var html = _pendingLoadHtml;
                    _pendingLoadHtml = null;
                    if (!string.IsNullOrEmpty(html))
                    {
                        _worldSeq++;
                        Post(new { cmd = "load", html, seq = _worldSeq });
                    }
                    else
                        Post(new { cmd = "focus" });
                    EngineVeil.Visibility = Visibility.Collapsed;
                    break;

                case "pages":
                    if (root.TryGetProperty("count", out var count) && count.TryGetInt32(out int n)
                        && n != _pageCount)
                    {
                        _pageCount = n;
                        PageCountChanged?.Invoke(n);
                    }
                    break;

                case "state":
                    ApplyState(root);
                    break;

                case "save":
                    {
                        // The save carries the world it was written in: a stale
                        // answer (the page answered a dump after the load already
                        // swapped worlds) never lands in the wrong tab.
                        string saved = root.TryGetProperty("html", out var savedHtml)
                            ? savedHtml.GetString() ?? string.Empty : string.Empty;
                        int seq = root.TryGetProperty("seq", out var sq) ? sq.GetInt32() : _worldSeq;
                        string title = root.TryGetProperty("title", out var ti)
                            ? ti.GetString() ?? string.Empty : string.Empty;
                        if (seq == _worldSeq && _activeTab >= 0 && _activeTab < _tabs.Count)
                        {
                            _tabs[_activeTab].Html = saved;
                            if (title.Length > 0) _tabs[_activeTab].Title = title;
                        }
                        if (_pendingSwitchTo is int to && to >= 0 && to < _tabs.Count)
                        {
                            _pendingSwitchTo = null;
                            ActivateEditorTabNow(to);
                        }
                        SaveSession();
                    }
                    break;

                case "title":
                    {
                        int seq = root.TryGetProperty("seq", out var tseq) ? tseq.GetInt32() : _worldSeq;
                        string title = root.TryGetProperty("title", out var tti)
                            ? tti.GetString() ?? string.Empty : string.Empty;
                        if (seq == _worldSeq && title.Length > 0
                            && _activeTab >= 0 && _activeTab < _tabs.Count
                            && !string.Equals(_tabs[_activeTab].Title, title, StringComparison.Ordinal))
                        {
                            // The tab's own label repaints off the flag - the strip
                            // never rebuilds for a name.
                            _tabs[_activeTab].Title = title;
                        }
                    }
                    break;

                case "thumbs":
                    {
                        // The page's rasterized pages, one data URL per page, tagged
                        // with the world they describe: a stale answer never paints
                        // the rail of a tab that took the floor meanwhile.
                        int seq = root.TryGetProperty("seq", out var thseq) ? thseq.GetInt32() : _worldSeq;
                        if (seq != _worldSeq) break;
                        var thumbs = new List<string>();
                        if (root.TryGetProperty("thumbs", out var arr) && arr.ValueKind == JsonValueKind.Array)
                            foreach (var el in arr.EnumerateArray())
                                thumbs.Add(el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : "");
                        if (_activeTab >= 0 && _activeTab < _tabs.Count)
                            _thumbCache[_tabs[_activeTab]] = thumbs.ToArray();
                        ThumbsChanged?.Invoke(thumbs.ToArray());
                    }
                    break;

                case "link":
                    if (root.TryGetProperty("url", out var linkUrl))
                    {
                        string url = linkUrl.GetString() ?? string.Empty;
                        if (url.Length > 0) LinkOpenRequested?.Invoke(url);
                    }
                    break;
            }
        }

        private void ApplyState(JsonElement r)
        {
            bool edit = !r.TryGetProperty("edit", out var ed) || ed.GetBoolean();
            if (edit)
            {
                if (r.TryGetProperty("font", out var f) && f.GetString() is { Length: > 0 } font
                    && !string.Equals(FontText.Text, font, StringComparison.Ordinal))
                    FontText.Text = font;
                if (r.TryGetProperty("size", out var sz) && sz.GetString() is { Length: > 0 } size
                    && !string.Equals(SizeBox.Text, size, StringComparison.Ordinal)
                    && !SizeBox.IsFocused)   // the reader is typing a value - the report never stomps it
                {
                    SizeBox.Text = size;
                    _lastAppliedSize = size;
                }
            }
            SetToggle(BoldBtn, Prop(r, "b"));
            SetToggle(ItalicBtn, Prop(r, "i"));
            SetToggle(UnderlineBtn, Prop(r, "u"));
            SetToggle(StrikeBtn, Prop(r, "s"));
            if (r.TryGetProperty("page", out var pg))
            {
                int p = pg.GetInt32();
                if (p != _activePage)
                {
                    _activePage = p;
                    ActivePageChanged?.Invoke(p);
                }
            }
        }

        private static bool Prop(JsonElement r, string name)
            => r.TryGetProperty(name, out var v) && v.GetBoolean();

        private static void SetToggle(ToggleButton tb, bool on) => tb.IsChecked = on;

        // ── Messages: ribbon -> page ─────────────────────────────────────────────

        private void Post(object msg)
        {
            if (!_pageReady || _web?.CoreWebView2 is null) return;
            try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); }
            catch { /* a closed engine eats the message; the next one retries */ }
        }

        // The click that opened a ribbon control took the keyboard with it;
        // hand it back so writing continues where the caret was - the reader
        // never has to click the page again just to keep typing.
        private void RefocusEditor()
        {
            var web = _web;
            if (web is null) return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                try { web.Focus(); }   // the WPF wrapper's Focus lands in the browser
                catch { /* a pane on its way out owes nobody focus */ }
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        // Every command hands the keyboard back to the page: the click that
        // summoned the button took it, and without the hand-back the reader
        // had to click the text again before typing went anywhere - and the
        // stray clicks kept landing wherever the caret used to be (v1.19.67).
        private void BoldBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "bold" }); RefocusEditor(); }
        private void ItalicBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "italic" }); RefocusEditor(); }
        private void UnderlineBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "underline" }); RefocusEditor(); }
        private void StrikeBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "strike" }); RefocusEditor(); }
        private void LinkBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "linkui" }); RefocusEditor(); }
        private void FootnoteBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "footnote" }); RefocusEditor(); }

        private void FontBtn_Click(object sender, RoutedEventArgs e)
        {
            if (FontBtn.IsChecked != true) return;
            HighlightCurrentFont();
            FontPopup.IsOpen = true;
        }

        // The caret button is now the ONE door to the size list: the input
        // beside it types, and nothing in it can open a dropdown (v1.19.69).
        private void SizeDropBtn_Click(object sender, RoutedEventArgs e)
        {
            HighlightCurrentSize();
            SizePopup.IsOpen = true;
        }

        private void SizeBox_GotFocus(object sender, RoutedEventArgs e) => SizeBox.SelectAll();

        private void SizeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplyTypedSize();
                RefocusEditor();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SizeBox.Text = _lastAppliedSize;
                SizeBox.SelectAll();
            }
        }

        // A value leaves the box only through Enter. Blur is not a commit -
        // the reader clicked away, not typed a size - so the field reverts.
        private void SizeBox_LostFocus(object sender, RoutedEventArgs e) => SizeBox.Text = _lastAppliedSize;

        // The typed value lands exactly where a picked size would: on the
        // selection, or - with the caret resting - on what gets typed next.
        // Junk reverts; the field is never a gate.
        private void ApplyTypedSize()
        {
            string raw = SizeBox.Text.Trim();
            if (!int.TryParse(raw, out int pt)) { SizeBox.Text = _lastAppliedSize; return; }
            pt = Math.Clamp(pt, 1, 999);
            string norm = pt.ToString();
            Post(new { cmd = "size", pt = norm });
            _lastAppliedSize = norm;
            SizeBox.Text = norm;
        }

        // One step up or down the same ladder the size list offers, applied to
        // the selection - or, with the caret resting, to what gets typed next.
        private void FontGrowBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "sizeStep", dir = 1 }); RefocusEditor(); }
        private void FontShrinkBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "sizeStep", dir = -1 }); RefocusEditor(); }

        private void HighlightCurrentFont()
        {
            string cur = FontText.Text;
            int idx = Array.FindIndex(FontChoices, f => string.Equals(f, cur, StringComparison.OrdinalIgnoreCase));
            // Highlighting the dial's own face must never APPLY anything: setting
            // the index fired SelectionChanged, and merely opening the size list
            // dressed the chip's size onto the reader's selection (v1.19.68).
            _syncingLists = true;
            try
            {
                FontList.SelectedIndex = idx;
                if (idx >= 0) FontList.ScrollIntoView(FontList.Items[idx]);
            }
            finally { _syncingLists = false; }
        }

        private void HighlightCurrentSize()
        {
            string cur = SizeBox.Text;
            int idx = Array.FindIndex(SizeChoices, s => string.Equals(s, cur, StringComparison.Ordinal));
            // Same law as the font dial: the highlight is not an application.
            _syncingLists = true;
            try
            {
                SizeList.SelectedIndex = idx;
                if (idx >= 0) SizeList.ScrollIntoView(SizeList.Items[idx]);
            }
            finally { _syncingLists = false; }
        }

        private void FontList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingLists) return;   // the dial's own highlight never applies
            if (FontList.SelectedItem is string font)
            {
                Post(new { cmd = "font", name = font });
                FontText.Text = font;
                FontPopup.IsOpen = false;
                RefocusEditor();
                // Reset so picking the same font again still fires next time.
                Dispatcher.BeginInvoke(() => FontList.SelectedIndex = -1,
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void SizeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingLists) return;   // the dial's own highlight never applies
            if (SizeList.SelectedItem is string size)
            {
                Post(new { cmd = "size", pt = size });
                SizeBox.Text = size;
                _lastAppliedSize = size;
                SizePopup.IsOpen = false;
                RefocusEditor();
                Dispatcher.BeginInvoke(() => SizeList.SelectedIndex = -1,
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void EditorBrowserBtn_Click(object sender, RoutedEventArgs e) => BrowserRequested?.Invoke();
        private void EditorPdfBtn_Click(object sender, RoutedEventArgs e) => PdfRequested?.Invoke();

        // ── The behind-text image ────────────────────────────────────────────────

        private void ImageBtn_Click(object sender, RoutedEventArgs e) => _ = PickImageAsync();

        private async Task PickImageAsync()
        {
            var dlg = new FileDialog(FileDialogMode.Open)
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                ShowImagePreview = true,
            };
            var owner = Window.GetWindow(this);
            bool ok = owner is not null ? dlg.ShowDialog(owner) == true : dlg.ShowDialog() == true;
            if (!ok) return;
            try
            {
                var fi = new FileInfo(dlg.FileName);
                if (fi.Length > 10 * 1024 * 1024)
                {
                    EngineStatus.Text = Loc("Str_Editor_ImageTooLarge");
                    EngineVeil.Visibility = Visibility.Visible;
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (EngineStatus.Text == Loc("Str_Editor_ImageTooLarge"))
                            EngineVeil.Visibility = Visibility.Collapsed;
                    }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    return;
                }
                string mime = Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
                {
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    ".jpg" => "image/jpeg",
                    ".jpeg" => "image/jpeg",
                    _ => "image/png",
                };
                byte[] bytes = File.ReadAllBytes(dlg.FileName);
                string src = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
                Post(new { cmd = "image", src });
                RefocusEditor();   // Delete removes the newborn image right away
            }
            catch (Exception ex)
            {
                EngineStatus.Text = ex.Message;
                EngineVeil.Visibility = Visibility.Visible;
            }
        }

        // ── Tabs: every document its own tab, the PDF editor's bargain ──────────
        // The page shows one world at a time; a switch asks the live world for
        // one last snapshot (a dump) and completes on its answer, so the last
        // keystrokes before a click are never lost. Saves carry the world
        // sequence they were written in, and a stale answer can never land in
        // the tab that took the floor meanwhile.

        private string ActiveTabHtml()
            => _activeTab >= 0 && _activeTab < _tabs.Count ? _tabs[_activeTab].Html : string.Empty;

        private void LoadEditorTabs()
        {
            _tabs.Clear();
            _activeTab = 0;
            _worldSeq = 0;
            _pendingSwitchTo = null;
            try
            {
                string p = Path.Combine(AppDataPaths.UserRoot, "texteditor", "session.json");
                if (File.Exists(p))
                {
                    var data = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(p));
                    if (data?.Tabs is { Count: > 0 })
                    {
                        foreach (var t in data.Tabs)
                            _tabs.Add(new EditorTab(t.Title ?? "", t.Html ?? "", _untitledLabel));
                        _activeTab = Math.Clamp(data.Active, 0, _tabs.Count - 1);
                        return;
                    }
                }
                // The v1.19.67 single-document cache migrates as the first tab.
                string old = Path.Combine(AppDataPaths.UserRoot, "texteditor", "session.html");
                if (File.Exists(old))
                    _tabs.Add(new EditorTab("", File.ReadAllText(old), _untitledLabel));
            }
            catch
            {
                // The session cache never takes the editor down.
            }
            if (_tabs.Count == 0)
                _tabs.Add(new EditorTab("", "", _untitledLabel));
        }

        private void SaveSession()
        {
            try
            {
                var data = new SessionData
                {
                    Tabs = _tabs.Select(t => new SessionTab { Title = t.Title, Html = t.Html }).ToList(),
                    Active = Math.Clamp(_activeTab, 0, Math.Max(0, _tabs.Count - 1)),
                };
                string dir = Path.Combine(AppDataPaths.UserRoot, "texteditor");
                Directory.CreateDirectory(dir);
                string tmp = Path.Combine(dir, "session.tmp");
                string dst = Path.Combine(dir, "session.json");
                File.WriteAllText(tmp, JsonSerializer.Serialize(data));
                try { File.Move(tmp, dst, true); }
                catch (IOException) { return; }                        // a busy target: the next save retries
                catch (UnauthorizedAccessException) { return; }        // the disk said no: the cache is a courtesy
            }
            catch
            {
                // The session cache never takes the editor down.
            }
        }

        /// <summary>The active tab's last known raster, or null when this tab
        /// has never drawn itself - the rail keeps still across switches
        /// instead of flashing empty on every landing (v1.19.71).</summary>
        private string[]? CachedThumbs()
            => _activeTab >= 0 && _activeTab < _tabs.Count
               && _thumbCache.TryGetValue(_tabs[_activeTab], out var cached) ? cached : null;

        /// <summary>Bind the strip to the editor's tabs. Called once, from the
        /// constructor - the ItemsControl repaints itself off the collection</summary>
        private void InitTabStrip() => EditorTabStrip.ItemsSource = _tabs;

        /// <summary>
        /// The one funnel. Every add, close, switch, drag-reorder and resize ends
        /// here, and it is the only thing that writes the strip's state - the
        /// reader's own RebuildTabStrip law, still called RebuildTabStrip.
        /// </summary>
        private void RebuildTabStrip()
        {
            if (EditorTabStrip == null || EditorTabBand == null) return;

            // The band is ALWAYS up (v1.19.70): the reader looks under the
            // toolbar for their tabs and they must be there - one document or
            // five. A band that only grows once a second document exists told
            // the reader "no tabs" on the very face that was supposed to answer
            // them, and the fresh-tab + hid behind the very rule that kept the
            // second document from ever being asked for. With the band up, the
            // -1 tucks the card's top border into it so the active tab and the
            // card read as one surface, the reader's own join.
            bool show = _tabs.Count > 0;
            EditorTabBand.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (CardRow != null)
                CardRow.Margin = new Thickness(0, show ? -1 : 8, 0, 0);

            // Which tabs fit at this width, before anything reads an edge.
            ApplyTabWindow();

            // First and last VISIBLE, not first and last in the list - with tabs
            // windowed out, the tab sitting on an edge is not the one at the end.
            bool chevron = EditorTabOverflowBtn.Visibility == Visibility.Visible;
            bool retro = show && Services.ThemeManager.Current == Services.Theme.SE98;
            var strip = _tabs.Where(t => t.IsStripVisible).ToList();
            foreach (var t in _tabs)
            {
                t.IsFirst = false;
                t.IsLast = false;
                t.UseRetroTabChrome = retro;
            }
            if (strip.Count > 0)
            {
                strip[0].IsFirst = true;
                bool hostFills = EditorTabStripHost.Width >=
                    Math.Max(0, EditorTabBand.ActualWidth - (chevron ? TabChevronWidth : 0) - TabNewWidth) - 0.5;
                strip[^1].IsLast = !chevron && hostFills;
            }
        }

        /// <summary>Decide which tabs are in the strip at the band's current
        /// width, and show or hide the chevron. The window is a contiguous RUN,
        /// not a set, and it shifts the least it can to keep the active tab on
        /// screen - the one invariant that matters.</summary>
        private void ApplyTabWindow()
        {
            int n = _tabs.Count;
            if (n == 0)
            {
                foreach (var t in _tabs) t.IsStripVisible = false;
                EditorTabOverflowBtn.Visibility = Visibility.Collapsed;
                EditorTabStripHost.Width = 0;
                return;
            }

            // ActualWidth is 0 until the band has been measured once; falling back
            // to the control's own width keeps the answer sane until it is.
            double avail = EditorTabBand.ActualWidth > 0 ? EditorTabBand.ActualWidth : ActualWidth;

            // Two passes, because the chevron's width changes the answer that
            // decides whether there is a chevron. The + is always in the band.
            int cap = (int)(avail / TabFloorWidth);
            bool overflow = cap < n;
            if (overflow)
            {
                cap = Math.Max(1, (int)((avail - TabChevronWidth - TabNewWidth) / TabFloorWidth));
                if (cap >= n) overflow = false;
            }

            EditorTabOverflowBtn.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;

            double stripAvail = Math.Max(0, avail - (overflow ? TabChevronWidth : 0) - TabNewWidth);
            int visibleCount = overflow ? cap : n;
            EditorTabStripHost.Width = Math.Max(TabFloorWidth, Math.Min(stripAvail, visibleCount * TabCeilingWidth));

            int start = 0;
            if (overflow)
            {
                start = Math.Max(0, Math.Min(_tabWindow, n - cap));
                int active = (_activeTab >= 0 && _activeTab < n) ? _activeTab : -1;
                if (active >= 0 && active < start) start = active;
                else if (active >= 0 && active > start + cap - 1) start = active - cap + 1;
                _tabWindow = start;
            }
            else
            {
                _tabWindow = 0;
                cap = n;
            }

            for (int i = 0; i < n; i++)
                _tabs[i].IsStripVisible = i >= start && i < start + cap;
        }

        /// <summary>The band was resized, so the strip may hold a different number
        /// of tabs. The reentrancy guard is not an optimization: RebuildTabStrip
        /// writes the card's margin and the band's visibility, either of which
        /// can raise SizeChanged again from inside this call.</summary>
        private void EditorTabBand_SizeChanged(object sender, SizeChangedEventArgs e) => TabBarResized();

        private void TabBarResized()
        {
            if (_tabs.Count == 0 || _inTabResize) return;
            _inTabResize = true;
            try { RebuildTabStrip(); }
            finally { _inTabResize = false; }
        }

        /// <summary>The chevron lists only tabs windowed out of the strip, the
        /// active one bold - built on each open, because titles move.</summary>
        private void EditorTabOverflow_Click(object sender, RoutedEventArgs e)
        {
            var menu = MakeEditorMenu();
            foreach (var t in _tabs)
            {
                if (t.IsStripVisible) continue;
                var tab = t;
                // Doubled, because a lone underscore in a MenuItem header is an
                // access-key marker - document names carry underscores all the time.
                var item = MakeEditorMenuItem(tab.TabLabel.Replace("_", "__"),
                    (_, _) => RequestEditorTabSwitch(_tabs.IndexOf(tab)));
                if (tab.IsActive) item.FontWeight = FontWeights.Bold;
                menu.Items.Add(item);
            }
            menu.PlacementTarget = EditorTabOverflowBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Code-created ContextMenus are popup roots and can miss the hosting
        // window's implicit style - the reader's own MakeThemedMenu law.
        private System.Windows.Controls.ContextMenu MakeEditorMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu();
            if (TryFindResource(typeof(System.Windows.Controls.ContextMenu)) is Style style)
                menu.Style = style;
            System.Windows.Media.TextOptions.SetTextFormattingMode(menu, System.Windows.Media.TextFormattingMode.Display);
            System.Windows.Media.TextOptions.SetTextRenderingMode(menu, System.Windows.Media.TextRenderingMode.Grayscale);
            return menu;
        }

        private System.Windows.Controls.MenuItem MakeEditorMenuItem(string header, RoutedEventHandler onClick)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += onClick;
            return item;
        }

        /// <summary>The reader clicked a tab: the live world is asked for one
        /// last snapshot before the next world loads - the page answers with
        /// its save, and the switch completes there.</summary>
        public void RequestEditorTabSwitch(int to)
        {
            if (to < 0 || to >= _tabs.Count || to == _activeTab) return;
            if (!_pageReady) { ActivateEditorTabNow(to); return; }
            _pendingSwitchTo = to;
            Post(new { cmd = "dump" });
        }

        private void ActivateEditorTabNow(int to)
        {
            if (to < 0 || to >= _tabs.Count) return;
            _activeTab = to;
            RebuildTabStrip();
            ThumbsChanged?.Invoke(CachedThumbs());   // the tab's own last raster paints now; a fresh one follows
            if (_pageReady)
            {
                _worldSeq++;
                Post(new { cmd = "load", html = _tabs[to].Html, seq = _worldSeq });
            }
            else
            {
                _pendingLoadHtml = _tabs[to].Html;
            }
        }

        /// <summary>New: an empty document in its own tab - the main toolbar's
        /// New button while the editor leads (v1.19.68).</summary>
        public void OpenNewTab()
        {
            _tabs.Add(new EditorTab("", "", _untitledLabel));
            RebuildTabStrip();
            int to = _tabs.Count - 1;
            if (!_pageReady) { ActivateEditorTabNow(to); SaveSession(); return; }
            _pendingSwitchTo = to;
            Post(new { cmd = "dump" });   // the current world saves first; the switch completes on its answer
        }

        private void CloseTab(EditorTab s)
        {
            int index = _tabs.IndexOf(s);
            if (index < 0) return;
            bool closingActive = index == _activeTab;
            _tabs.RemoveAt(index);
            _thumbCache.Remove(s);   // a closed document's pictures leave with it
            if (_tabs.Count == 0)
                _tabs.Add(new EditorTab("", "", _untitledLabel));
            if (closingActive)
            {
                _activeTab = -1;                      // the outgoing world's save has no home - drop it
                int to = Math.Min(index, _tabs.Count - 1);
                if (_pageReady)
                {
                    _pendingSwitchTo = to;
                    Post(new { cmd = "dump" });       // a courtesy: nothing left to save, but the flow is one
                }
                else
                {
                    ActivateEditorTabNow(to);
                }
            }
            else
            {
                if (index < _activeTab) _activeTab--;
                RebuildTabStrip();
            }
            SaveSession();
        }

        private void CloseOtherTabs(EditorTab keep)
        {
            // Backward, so a close never shifts the index of a tab still waiting.
            for (int i = _tabs.Count - 1; i >= 0; i--)
                if (!ReferenceEquals(_tabs[i], keep)) CloseTab(_tabs[i]);
        }

        // ── Tab gestures: the reader's own laws ──────────────────────────────
        // Left-click switches on mouse-UP, so a press can begin a drag without
        // switching first; the middle button closes; the right button names the
        // two closes every document strip offers.

        private void EditorTab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not EditorTab s) return;
            if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; CloseTab(s); }
        }

        private void EditorTab_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not EditorTab s) return;
            var menu = MakeEditorMenu();
            menu.Items.Add(MakeEditorMenuItem(Loc("Str_Ctx_CloseTab"), (_, _) => CloseTab(s)));
            var others = MakeEditorMenuItem(Loc("Str_Ctx_CloseOthers"), (_, _) => CloseOtherTabs(s));
            others.IsEnabled = _tabs.Count > 1;
            menu.Items.Add(others);
            menu.PlacementTarget = fe;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void EditorTabClose_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b && b.Tag is EditorTab s) CloseTab(s);
        }

        private void EditorTabNewBtn_Click(object sender, RoutedEventArgs e) => OpenNewTab();

        // ── Drag: reorder within the strip ───────────────────────────────────
        // Arm on press; past the threshold the grabbed tab glues to the cursor
        // and its neighbors glide aside as it crosses their layout-slot
        // midpoints. A plain click still switches on release.

        private FrameworkElement? TabContainer(EditorTab s)
            => EditorTabStrip?.ItemContainerGenerator.ContainerFromItem(s) as FrameworkElement;

        /// <summary>Did the press land on the close chip rather than the tab?</summary>
        private static bool InsideButton(object src)
        {
            var d = src as System.Windows.DependencyObject;
            while (d != null && d is not System.Windows.Controls.Button && d is not Window)
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            return d is System.Windows.Controls.Button;
        }

        /// <summary>Midpoint X of a tab's LAYOUT slot (ignores any in-flight slide).</summary>
        private static double LayoutMidX(FrameworkElement fe)
        {
            var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(fe);
            return slot.X + slot.Width / 2;
        }

        private static void SetTabOffsetX(FrameworkElement tab, double x)
        {
            if (tab.RenderTransform is not System.Windows.Media.TranslateTransform tt)
            {
                tt = new System.Windows.Media.TranslateTransform();
                tab.RenderTransform = tt;
            }
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            tt.X = x;
        }

        private static void AnimateTabSlide(FrameworkElement? tab, double fromX)
        {
            if (tab == null) return;
            if (tab.RenderTransform is not System.Windows.Media.TranslateTransform tt)
            {
                tt = new System.Windows.Media.TranslateTransform();
                tab.RenderTransform = tt;
            }
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            var anim = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0,
                new Duration(TimeSpan.FromMilliseconds(140)))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
        }

        private void CleanupTabTransforms()
        {
            foreach (var s in _tabs)
                if (TabContainer(s) is { } c)
                {
                    c.RenderTransform = null;
                    Panel.SetZIndex(c, 0);
                }
        }

        private void EditorTab_DragDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement bd || bd.DataContext is not EditorTab s) return;
            if (InsideButton(e.OriginalSource)) return;   // the close chip handles its own click
            _tabDragSession = s;
            _tabDragStart = e.GetPosition(EditorTabStrip);
            _tabGrabDX = e.GetPosition(bd).X;
            _tabDragging = false;
            bd.CaptureMouse();
            // Own the press entirely so the capture, not the caption hit-test,
            // drives the drag - which is what makes it Y-independent.
            e.Handled = true;
        }

        private void EditorTab_DragMove(object sender, MouseEventArgs e)
        {
            if (sender is not FrameworkElement bd || !bd.IsMouseCaptured || _tabDragSession is null) return;
            var cont = TabContainer(_tabDragSession);
            if (cont == null) return;

            double x = e.GetPosition(EditorTabStrip).X;
            if (!_tabDragging && Math.Abs(x - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            _tabDragging = true;
            Panel.SetZIndex(cont, 3);   // the grabbed tab rides above its neighbors

            int cur = _tabs.IndexOf(_tabDragSession);
            if (cur < 0) return;
            double slide = cont.ActualWidth;
            double rawLeft = x - _tabGrabDX;
            double leftEdge = rawLeft;
            double rightEdge = rawLeft + cont.ActualWidth;
            double maxLeft = Math.Max(0, EditorTabStrip.ActualWidth - slide);
            double renderLeft = Math.Min(Math.Max(0, rawLeft), maxLeft);

            // Swap when the ADVANCING edge crosses a neighbor's layout-slot
            // midpoint - edge against midpoint gives natural hysteresis.
            bool swapped = false;
            if (cur + 1 < _tabs.Count && TabContainer(_tabs[cur + 1]) is { } right && rightEdge > LayoutMidX(right))
            {
                _tabs.Move(cur + 1, cur);
                AnimateTabSlide(TabContainer(_tabs[cur]), slide);    // it jumped left; glide it in
                swapped = true;
            }
            else if (cur - 1 >= 0 && TabContainer(_tabs[cur - 1]) is { } left && leftEdge < LayoutMidX(left))
            {
                _tabs.Move(cur - 1, cur);
                AnimateTabSlide(TabContainer(_tabs[cur]), -slide);   // it jumped right; glide it in
                swapped = true;
            }

            if (swapped) EditorTabStrip.UpdateLayout();
            var dragged = TabContainer(_tabDragSession);
            if (dragged == null) return;
            var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(dragged);
            SetTabOffsetX(dragged, renderLeft - slot.X);
        }

        private void EditorTab_DragUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement bd || !bd.IsMouseCaptured) return;
            bd.ReleaseMouseCapture();
            bool wasDragging = _tabDragging;
            var s = _tabDragSession;
            _tabDragSession = null;
            _tabDragging = false;

            if (!wasDragging)
            {
                if (s != null) RequestEditorTabSwitch(_tabs.IndexOf(s));
                return;
            }

            RebuildTabStrip();   // a reorder may have moved the active tab on or off an edge
            SaveSession();

            // Settle the grabbed tab from its dragged offset into its final slot.
            var cont = s != null ? TabContainer(s) : null;
            if (cont?.RenderTransform is System.Windows.Media.TranslateTransform tt && Math.Abs(tt.X) > 0.5)
            {
                var settle = new System.Windows.Media.Animation.DoubleAnimation(0,
                    new Duration(TimeSpan.FromMilliseconds(120)))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                };
                settle.Completed += (_, _) => CleanupTabTransforms();
                tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, settle);
            }
            else CleanupTabTransforms();
        }

        // ── Host-facing surface ──────────────────────────────────────────────────

        /// <summary>The sidebar asked for a page: the editor scrolls to it.</summary>
        public void ScrollToPage(int pageNumber) => Post(new { cmd = "scroll", n = pageNumber });

        /// <summary>
        /// Ctrl+Z answered from the host. The window's OnPreviewKeyDown tunnels
        /// FIRST - before this control's own PreviewKeyDown and before the page's
        /// own keydown ever sees the key - and while the editor leads it used to
        /// hand the chord to the PDF viewer's annotation undo, whose empty stack
        /// answered "Nothing to undo" (v1.19.69). The window routes the chord
        /// here now, and it rides the bridge like every ribbon command. The
        /// accelerator is consumed by the route, so the page's own keydown -
        /// which also answers Ctrl+Z when the page itself sees it first - never
        /// fires twice for one press.
        /// </summary>
        public void UndoExt() { Post(new { cmd = "undo" }); RefocusEditor(); }

        /// <summary>Ctrl+Shift+Z / Ctrl+Y, the same route, the same single fire.</summary>
        public void RedoExt() { Post(new { cmd = "redo" }); RefocusEditor(); }
    }
}
