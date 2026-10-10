using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Avalanche.Services;

namespace Avalanche.Controls
{
    // The text editor's host (v1.19.72): a ribbon of real writing controls and one
    // WebView2 running the embedded editor document (Controls/TextEditorDocument.cs).
    // The split is deliberate - the ribbon is WPF and speaks theme brushes like every
    // other bar in the app, while the page itself is a standalone Quill.js v2 surface
    // where the document model, the caret, the history, footnotes, links and images
    // are the engine's own first-class citizens instead of fights. The two halves
    // talk over WebView2 messages: the ribbon posts commands (bold, font, size,
    // link, footnote, image), the page posts facts back (what the caret is wearing,
    // what to save, links to open, the sheet's own raster).
    // Chromium never ships in the payload - the Evergreen runtime the browser pane
    // already rides wakes here too, lazily, and suspends when the pane hides.
    // v1.19.73: the ribbon grew the block tools - three header levels, bullet,
    // number, the alpha sub-number, quote, every one press-again-to-undo - and
    // grew to hold them, twice its old size. The classic four chords (select
    // all, copy, cut, paste) reach the sheet whichever control holds the
    // keyboard: the window shortcut chain stands down for the writing pane the
    // way it always has for the browser pane, paste reads the Windows clipboard
    // here and hands the sheet its fragment, and copy and cut come back as
    // 'clip' text on their way to the clipboard.
    public partial class TextEditorControl : UserControl
    {
        private CoreWebView2Environment? _env;
        private WebView2? _web;
        private bool _envBuilding;
        private bool _pageReady;
        private string? _pendingLoadHtml;
        private string? _pendingSavePath;
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
        private bool _docInverted = App.GetSetting("EditorDocInvert") == "1";   // the sheet's remembered dark page (v1.19.80)
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
            ListPopup.Closed += (_, _) => ListBtn.IsChecked = false;
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
            // A WPF text control - the size box above all - keeps its own
            // classic chords: select-all, copy, cut and paste work on its text.
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (k)
            {
                case Key.Z:
                    e.Handled = true;
                    Post(new { cmd = mods.HasFlag(ModifierKeys.Shift) ? "redo" : "undo" });
                    break;
                case Key.Y:
                    e.Handled = true;
                    Post(new { cmd = "redo" });
                    break;
                // The four the reader reaches for without thinking (v1.19.73):
                // select all, copy, cut and paste, routed to the sheet whichever
                // control holds the keyboard. The window's shortcut chain stands
                // down for this pane (KeyboardShortcuts.cs), so these arrive for
                // every keystroke the ribbon or the sheet should answer.
                case Key.A:
                    e.Handled = true;
                    Post(new { cmd = "selectAll" });
                    break;
                case Key.C:
                    e.Handled = true;
                    Post(new { cmd = "copy" });
                    break;
                case Key.X:
                    e.Handled = true;
                    Post(new { cmd = "cut" });
                    break;
                case Key.V:
                    e.Handled = true;
                    PasteClipboardIntoPage();
                    break;
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
                    DocInvertChanged?.Invoke(_docInverted);   // the host's moon wears the remembered face
                    Post(new { cmd = "invert", on = _docInverted });   // the remembered face rides every fresh sheet
                    ApplyInvertChrome(_docInverted);   // the card's ring goes dark with the sheet (v1.19.82)
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
                        if (seq == _worldSeq && _activeTab >= 0 && _activeTab < _tabs.Count
                            && _pendingSavePath is { Length: > 0 })
                            WriteEditorFile(saved, _tabs[_activeTab].Title);
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
                        if (thumbs.Contains(string.Empty))
                            _ = RefreshThumbnailsAsync();   // the sheet's canvas refused: the host paints instead
                    }
                    break;

                case "clip":
                    // Copy and cut come back as text: the clipboard write
                    // happens here, on the UI thread, where the Windows
                    // clipboard belongs. An empty selection sent nothing.
                    if (root.TryGetProperty("text", out var clipText)
                        && clipText.GetString() is { Length: > 0 } clip)
                    {
                        try { Clipboard.SetText(clip); }
                        catch { /* the clipboard occasionally refuses; the next copy retries */ }
                    }
                    break;

                case "link":
                    if (root.TryGetProperty("url", out var linkUrl))
                    {
                        string url = linkUrl.GetString() ?? string.Empty;
                        if (url.Length > 0) LinkOpenRequested?.Invoke(url);
                    }
                    break;

                case "ai_timer_grammar_scan":
                    RunAiGrammarScan(root);
                    break;

                case "ai_fix_selection":
                    RunAiSelection(root, "fix");
                    break;

                case "ai_rewrite":
                    RunAiSelection(root, "rewrite");
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
            // The block layer (v1.19.73): the header level, the list kind,
            // the list layer and the quote - the lights show what the
            // caret is actually wearing, page's word is final. The lists
            // dropdown (v1.19.75) lights while any list kind is on.
            int header = r.TryGetProperty("h", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt32() : 0;
            // v1.19.79: two header levels, two lights - H1 rides Quill's
            // header:1, H2 rides header:2, the lone H and its third seat gone.
            SetToggle(H1Btn, header == 1);
            SetToggle(H2Btn, header == 2);
            string listState = r.TryGetProperty("list", out var lsv) ? lsv.GetString() ?? string.Empty : string.Empty;
            SetToggle(ListBtn, listState == "bullet" || listState == "ordered");
            SetToggle(QuoteBtn, Prop(r, "quote"));
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
        // The block tools (v1.19.73): the page owns every toggle decision -
        // the ribbon only speaks the chord and relights off the state report.
        private void H1Btn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "header", level = 1 }); RefocusEditor(); }
        private void H2Btn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "header", level = 2 }); RefocusEditor(); }
        // The lists dropdown (v1.19.75): picking a kind speaks the same
        // bullet / number / sub-number chord the old buttons spoke; the page
        // still owns every toggle decision, the light rides the report.
        private void ListBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ListBtn.IsChecked != true) return;
            ListPopup.IsOpen = true;
        }

        private void ListKinds_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListKinds.SelectedItem is ListBoxItem it && it.Tag is string cmd)
            {
                Post(new { cmd });
                RefocusEditor();
                ListPopup.IsOpen = false;
                Dispatcher.BeginInvoke(() => ListKinds.SelectedIndex = -1,
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        private void QuoteBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "quote" }); RefocusEditor(); }
        private void SpacingBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "spacing" }); RefocusEditor(); }
        // The alignment trio (v1.19.80): Quill's own align format answers.
        private void AlignLeftBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "alignLeft" }); RefocusEditor(); }
        private void AlignCenterBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "alignCenter" }); RefocusEditor(); }
        private void AlignJustifyBtn_Click(object sender, RoutedEventArgs e) { Post(new { cmd = "alignJustify" }); RefocusEditor(); }
        // The inverted page (v1.19.80, retaken v1.19.81): one class on the
        // sheet's body, the choice kept in the settings store so it outlives
        // the run. No button of its own any more - the host's moon (under
        // the split-pane button) drives it while the editor leads; the
        // control wears the state and takes orders from there.
        public bool DocInverted => _docInverted;
        public event Action<bool>? DocInvertChanged;
        public void SetDocInverted(bool on)
        {
            if (_docInverted == on) return;
            _docInverted = on;
            App.SetSetting("EditorDocInvert", on ? "1" : "0");
            Post(new { cmd = "invert", on = _docInverted });
            ApplyInvertChrome(on);   // the card's ring follows the sheet (v1.19.82)
            DocInvertChanged?.Invoke(_docInverted);
            RefocusEditor();
        }
        // The card's ring follows the sheet (v1.19.82): with the page inverted,
        // the host card's own light border and backing - PaneBorderBrush and
        // BgCanvas, near-white in the light themes - wrapped the black page in
        // a pale halo the reader kept calling a border. While the night is on
        // the card wears the desk's own gray, so the only edge on screen is the
        // page's own shadow; night off, the theme's brushes come back.
        private void ApplyInvertChrome(bool on)
        {
            if (on)
            {
                var desk = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3D, 0x40, 0x46));
                desk.Freeze();
                SheetCard.BorderBrush = desk;
                SheetCard.Background = desk;
            }
            else
            {
                SheetCard.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "PaneBorderBrush");
                SheetCard.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BgCanvas");
            }
        }
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

        // A click lands the caret where it hit - which unselected the
        // GotFocus select-all and left the reader typing INTO the old value
        // (v1.19.73). The first click focuses and selects everything instead,
        // so typing replaces the number the way Tab-focus always did; further
        // clicks place the caret like any text box.
        private void SizeBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!SizeBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                SizeBox.Focus();
                SizeBox.SelectAll();
            }
        }

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

        // -- The clipboard, ribbon to sheet ---------------------------------------

        // Paste reads the Windows clipboard ONCE here - the WebView2 page
        // cannot read the system clipboard on its own - and hands the sheet
        // whatever it holds: the source app's rich HTML fragment first, then
        // plain text, then an image as the data URL the sheet already knows
        // how to wear. Nothing held means nothing pastes.
        private void PasteClipboardIntoPage()
        {
            string? html = ClipboardHtmlFragment();
            if (html is not null) { Post(new { cmd = "paste", html }); return; }
            string text = string.Empty;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
            catch { /* a locked clipboard owes nobody a paste */ }
            if (text.Length > 0) { Post(new { cmd = "paste", text }); return; }
            try
            {
                if (Clipboard.ContainsImage())
                {
                    var source = Clipboard.GetImage();
                    if (source is not null)
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(source));
                        using var ms = new MemoryStream();
                        encoder.Save(ms);
                        Post(new { cmd = "pasteImage", src = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray()) });
                    }
                }
            }
            catch { /* no image survives encoding: nothing pastes */ }
        }

        // CF_HTML carries BYTE offsets into a UTF-8 payload: slice the bytes,
        // never the decoded string, or the fragment lands mid-character the
        // first time a pasted page used a non-ASCII letter before the marker.
        private static string? ClipboardHtmlFragment()
        {
            try
            {
                if (!Clipboard.ContainsText(TextDataFormat.Html)) return null;
                string raw = Clipboard.GetText(TextDataFormat.Html);
                int start = -1, end = -1;
                foreach (string line in raw.Split('\n'))
                {
                    string head = line.TrimEnd('\r');
                    if (head.StartsWith("StartFragment:", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(head.Substring(14).Trim(), out int s)) start = s;
                    else if (head.StartsWith("EndFragment:", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(head.Substring(12).Trim(), out int en)) end = en;
                }
                if (start < 0 || end <= start) return null;
                byte[] bytes = Encoding.UTF8.GetBytes(raw);
                if (start >= bytes.Length || end > bytes.Length) return null;
                string fragment = Encoding.UTF8.GetString(bytes, start, end - start);
                return fragment.Trim().Length > 0 ? fragment : null;
            }
            catch
            {
                return null;
            }
        }

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

        // The host's own raster (v1.19.79): the sheet's SVG paint taints its
        // canvas in WebView2 (a foreignObject security rule), so its empty
        // answer now calls THIS - the engine's native page capture, cached
        // and painted like any other raster. Fired when the sheet's thumbs
        // arrive carrying empties: on load, on tab switch, and after the
        // edit idle timer - every moment the rail could be staring at a stub.
        public async System.Threading.Tasks.Task RefreshThumbnailsAsync()
        {
            if (!_pageReady || _web?.CoreWebView2 is null) return;
            try
            {
                using var ms = new MemoryStream();
                await _web.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, ms);
                ms.Position = 0;
                string dataUrl = await CropToPageAsync(ms);   // the rail wears a page, not the whole scroll
                if (_activeTab >= 0 && _activeTab < _tabs.Count)
                    _thumbCache[_tabs[_activeTab]] = new[] { dataUrl };
                ThumbsChanged?.Invoke(new[] { dataUrl });
            }
            catch
            {
                // a refused capture keeps the rail as it was
            }
        }

        // The page, not the whole scroll (v1.19.80): CapturePreviewAsync rides
        // the full document height, so the raster comes home carved to the
        // sheet's own Letter geometry - 816 by 1056 CSS pixels, centered,
        // 24px of breath above. The sheet's own numbers, scaled by whatever
        // the capture rode in at; a silent answer or a narrow window keeps
        // the full raster, because the courtesy never outranks the picture.
        private async System.Threading.Tasks.Task<string> CropToPageAsync(MemoryStream png)
        {
            var core = _web?.CoreWebView2;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = png;
                bmp.EndInit();
                bmp.Freeze();
                double clientW = 0;
                string? raw = core is null ? null
                    : await core.ExecuteScriptAsync("String(document.documentElement.clientWidth)");
                if (!double.TryParse(raw?.Trim('"'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out clientW))
                    clientW = 0;
                if (clientW > 0 && bmp.PixelWidth > 16 && bmp.PixelHeight > 16)
                {
                    double scale = bmp.PixelWidth / clientW;
                    int pageW = (int)Math.Round(816.0 * scale);
                    int pageH = (int)Math.Round(1056.0 * scale);
                    int top = (int)Math.Round(24.0 * scale);
                    int x0 = Math.Max(0, (bmp.PixelWidth - pageW) / 2);
                    int y0 = Math.Max(0, Math.Min(top, bmp.PixelHeight - 16));
                    int w = Math.Min(pageW, bmp.PixelWidth - x0);
                    int h = Math.Min(pageH, bmp.PixelHeight - y0);
                    if (w >= 16 && h >= 16)
                    {
                        var cropped = new CroppedBitmap((BitmapSource)bmp, new Int32Rect(x0, y0, w, h));
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(cropped));
                        using var outMs = new MemoryStream();
                        enc.Save(outMs);
                        return "data:image/png;base64," + Convert.ToBase64String(outMs.ToArray());
                    }
                }
            }
            catch
            {
                // a refused carve still paints the full capture below
            }
            png.Position = 0;
            return "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
        }

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

        // The ribbon's own file operations (v1.19.81): the main window's
        // buttons call straight in - new tab, open and save live in THE
        // RIBBON, beside New and before the Browser button, nowhere else.

        // ── Save and Open (v1.19.75): the reader's own files ─────────────────
        // Save asks where with the app's own Windows-style prompt, then the
        // sheet's save pipeline delivers the bytes: the dump answers, the
        // file is written on its arrival, seq-guarded like every save.
        public void SaveEditorDocument()
        {
            if (_activeTab < 0 || _activeTab >= _tabs.Count) return;
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = "HTML document|*.html;*.htm|Text file|*.txt|All files|*.*",
                FileName = EditorFileName(_tabs[_activeTab].Title),
                OverwritePrompt = true,
            };
            var owner = Window.GetWindow(this);
            bool ok = owner is not null ? dlg.ShowDialog(owner) == true : dlg.ShowDialog() == true;
            if (!ok) return;
            _pendingSavePath = dlg.FileName;
            if (_pageReady) { Post(new { cmd = "dump" }); return; }
            WriteEditorFile(_tabs[_activeTab].Html, _tabs[_activeTab].Title);
        }

        // Open reads an .html (or plain .txt) back as a document of its own,
        // in a fresh tab wearing the file's name.
        public void OpenEditorDocument()
        {
            var dlg = new FileDialog(FileDialogMode.Open)
            {
                Filter = "HTML document|*.html;*.htm|Text file|*.txt|All files|*.*",
                CheckFileExists = true,
            };
            var owner = Window.GetWindow(this);
            bool ok = owner is not null ? dlg.ShowDialog(owner) == true : dlg.ShowDialog() == true;
            if (!ok) return;
            string text;
            try { text = File.ReadAllText(dlg.FileName); }
            catch { return; }
            _tabs.Add(new EditorTab(Path.GetFileNameWithoutExtension(dlg.FileName), OpenedHtml(text), _untitledLabel));
            RebuildTabStrip();
            int to = _tabs.Count - 1;
            if (!_pageReady) { ActivateEditorTabNow(to); SaveSession(); return; }
            _pendingSwitchTo = to;
            Post(new { cmd = "dump" });
        }

        // The pending file write happens once: the save case hands over the
        // exact document it stored, and the path clears either way.
        private void WriteEditorFile(string rawHtml, string title)
        {
            string? path = _pendingSavePath;
            _pendingSavePath = null;
            if (path is null || path.Length == 0) return;
            try { File.WriteAllText(path, EditorFileHtml(rawHtml, title), new UTF8Encoding(false)); }
            catch { /* a refused path must never take the editor down */ }
        }

        // The file gets a real document shell: the sheet's save is a body
        // fragment, and the reader's file should open whole in any browser.
        private static string EditorFileHtml(string raw, string title)
        {
            string html = raw ?? string.Empty;
            int body = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (body >= 0)
            {
                int s = html.IndexOf('>', body);
                int e = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (s >= 0 && e > s) html = html[(s + 1)..e];
            }
            string safeTitle = (title ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            return "<!DOCTYPE html>\r\n<html>\r\n<head>\r\n<meta charset=\"utf-8\">\r\n<title>" + safeTitle + "</title>\r\n</head>\r\n<body>\r\n"
                + html + "\r\n</body>\r\n</html>\r\n";
        }

        private static string EditorFileName(string title)
        {
            string t = (title ?? "").Trim();
            if (t.Length == 0) return "document.html";
            foreach (char c in Path.GetInvalidFileNameChars()) t = t.Replace(c, '_');
            if (t.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)) return t;
            return t + ".html";
        }

        // An opened file becomes sheet content: a full HTML document gives
        // up its body, a bare fragment passes as-is, plain text wears
        // paragraphs so the sheet receives it line for line.
        private static string OpenedHtml(string text)
        {
            string t = text ?? string.Empty;
            int body = t.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (body >= 0)
            {
                int s = t.IndexOf('>', body);
                int e = t.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (s >= 0 && e > s) return t[(s + 1)..e];
            }
            bool looksHtml = t.Contains("<p>") || t.Contains("<div") || t.Contains("<br")
                || t.Contains("<h1") || t.Contains("<ol") || t.Contains("<ul");
            if (looksHtml) return t;
            var sb = new StringBuilder();
            foreach (string line in t.Replace("\r\n", "\n").Split('\n'))
            {
                string esc = line.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
                sb.Append(esc.Length == 0 ? "<p><br></p>" : "<p>" + esc + "</p>");
            }
            return sb.ToString();
        }

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

        // ── The editor's second brain (v1.19.77) ─────────────────────────────
        // Two doors, both opened by the page, both answered through the
        // sheet's own dials (EditorGrammar proofs, EditorRewrite rewrites). The quiet door: five
        // seconds after the reader stops typing, the page sends ONE batched
        // scan of the whole document - never a keystroke, an untouched
        // document never asks twice - and the answer dresses every stumble
        // in wavy red at once. The loud door: a selection longer than three
        // characters raises the bubble, and a button click is the only thing
        // that ever bills a rewrite. Both answers ride back tagged with the
        // world they were asked in; a stale answer never dresses the wrong
        // tab, and a failed scan answers empty rather than loud.

        private Features.AI.AiSettingsViewModel? _aiSettings;

        /// <summary>The named surface's dial, resolved fresh every call:
        /// the settings panel stays the single source of the base config,
        /// the surface dial owns only who answers - the same law every other
        /// surface lives by. v1.19.78: the sheet's seat is two dials now -
        /// the proofreader (EditorGrammar) and the rewriter (EditorRewrite) -
        /// so the quiet door and the loud door can bill different brains.</summary>
        private Features.AI.AiProviderConfig AiConfig(Features.AI.AiSurface surface)
        {
            _aiSettings ??= new Features.AI.AiSettingsViewModel();
            _aiSettings.Load();
            return Features.AI.AiSurfaceModels.Configure(
                _aiSettings.ToGenConfig(), surface);
        }

        private void RunAiGrammarScan(JsonElement root)
        {
            int seq = root.TryGetProperty("seq", out var sq) ? sq.GetInt32() : -1;
            string text = root.TryGetProperty("text", out var tx)
                ? tx.GetString() ?? string.Empty : string.Empty;
            var ignored = new List<string>();
            if (root.TryGetProperty("ignored", out var ig) && ig.ValueKind == JsonValueKind.Array)
                foreach (var el in ig.EnumerateArray())
                {
                    string w = el.ValueKind == JsonValueKind.String
                        ? el.GetString() ?? string.Empty : string.Empty;
                    if (w.Length > 0) ignored.Add(w);
                }
            _ = RunAiGrammarScanAsync(seq, text, ignored);
        }

        private async System.Threading.Tasks.Task RunAiGrammarScanAsync(
            int seq, string text, List<string> ignored)
        {
            try
            {
                // A document past the cap scans its opening stretch: one
                // batched request stays one batched request, whatever the
                // document grew to.
                if (text.Length > 24000) text = text[..24000];
                string ignoredList = ignored.Count == 0
                    ? "(none)"
                    : string.Join(", ", ignored.Take(200));
                string system =
                    "You are an expert copyeditor and proofreader.\n" +
                    "Scan the provided text and identify all misspelled words, poor word choices, and grammatical mistakes.\n" +
                    "DO NOT flag words in this ignored list: [" + ignoredList + "].\n" +
                    "\n" +
                    "Output ONLY valid JSON with this exact schema (no markdown, no conversational text):\n" +
                    "{\n" +
                    "  \"errors\": [\n" +
                    "    {\n" +
                    "      \"word\": \"exact misspelled/poor word in text\",\n" +
                    "      \"suggestion\": \"corrected replacement\",\n" +
                    "      \"reason\": \"Spelling|Grammar|Word Choice\"\n" +
                    "    }\n" +
                    "  ]\n" +
                    "}\n" +
                    "If there are no errors, return: {\"errors\": []}";
                var config = AiConfig(Features.AI.AiSurface.EditorGrammar);
                var answer = await Features.AI.AiProviderFactory.CreateProvider(config.ProviderType)
                    .GetChatCompletionAsync(
                        system,
                        new List<Features.AI.ChatMessage>
                        {
                            new Features.AI.ChatMessage
                            {
                                MessageRole = Features.AI.ChatMessage.Role.User,
                                Content = text
                            }
                        },
                        new List<Features.AI.DocumentChunk>(),
                        "",
                        config,
                        System.Threading.CancellationToken.None);
                var errors = ParseAiErrors(answer.Answer);
                Post(new
                {
                    cmd = "aiScanResult",
                    seq,
                    errors = errors.Select(e => new
                    {
                        word = e.Word,
                        suggestion = e.Suggestion,
                        reason = e.Reason
                    }).ToList()
                });
            }
            catch
            {
                // A scan that dies answers empty: the sheet keeps its prose
                // undressed, and the next idle pause asks again.
                Post(new { cmd = "aiScanResult", seq, errors = Array.Empty<object>() });
            }
        }

        private sealed class AiGrammarError
        {
            public string Word { get; set; } = string.Empty;
            public string Suggestion { get; set; } = string.Empty;
            public string Reason { get; set; } = string.Empty;
        }

        /// <summary>The model's answer wears markdown fences some days and
        /// commentary on others; the JSON is wherever its outermost braces
        /// are. Words without a suggestion, or longer than a phrase, never
        /// dress the page.</summary>
        private static List<AiGrammarError> ParseAiErrors(string raw)
        {
            var errors = new List<AiGrammarError>();
            if (string.IsNullOrWhiteSpace(raw)) return errors;
            string s = raw.Trim();
            int a = s.IndexOf('{');
            int b = s.LastIndexOf('}');
            if (a < 0 || b <= a) return errors;
            try
            {
                using var doc = JsonDocument.Parse(s[a..(b + 1)]);
                if (doc.RootElement.TryGetProperty("errors", out var arr)
                    && arr.ValueKind == JsonValueKind.Array)
                    foreach (var el in arr.EnumerateArray())
                    {
                        string word = el.TryGetProperty("word", out var wv)
                            ? wv.GetString() ?? string.Empty : string.Empty;
                        string sug = el.TryGetProperty("suggestion", out var sv)
                            ? sv.GetString() ?? string.Empty : string.Empty;
                        string reason = el.TryGetProperty("reason", out var rv)
                            ? rv.GetString() ?? string.Empty : string.Empty;
                        if (word.Length > 0 && sug.Length > 0 && word.Length < 80)
                            errors.Add(new AiGrammarError
                            {
                                Word = word,
                                Suggestion = sug,
                                Reason = reason
                            });
                    }
            }
            catch
            {
                // an answer that will not parse dresses nothing
            }
            return errors;
        }

        private void RunAiSelection(JsonElement root, string kind)
        {
            int seq = root.TryGetProperty("seq", out var sq) ? sq.GetInt32() : -1;
            string text = root.TryGetProperty("text", out var tx)
                ? tx.GetString() ?? string.Empty : string.Empty;
            string style = root.TryGetProperty("style", out var st)
                ? st.GetString() ?? string.Empty : string.Empty;
            _ = RunAiSelectionAsync(seq, text, style, kind);
        }

        private static string AiRewriteSystem(string style) => style switch
        {
            "professional" =>
                "You are a professional rewriter. Rewrite the provided text crisp, " +
                "direct and active-voiced, workplace-appropriate, clear and polite. " +
                "Keep the meaning exactly. " +
                "Return ONLY the rewritten text - no quotes, no explanations, no markdown fences.",
            "simple" =>
                "You are a plain-language rewriter. Rewrite the provided text in plain " +
                "English at an 8th-grade reading level (Flesch-Kincaid 60 or higher): " +
                "short words, direct active sentences, no jargon. Keep the meaning " +
                "exactly - do not add or drop facts. " +
                "Return ONLY the rewritten text - no quotes, no explanations, no markdown fences.",
            "academic" =>
                "You are an academic rewriter. Rewrite the provided text with disciplined, " +
                "scholarly vocabulary and formal analytical framing. Keep the meaning " +
                "exactly. " +
                "Return ONLY the rewritten text - no quotes, no explanations, no markdown fences.",
            "jargon" =>
                "You are a jargon rewriter. Rewrite the provided text as deliberately " +
                "dense, bureaucratic prose - heavy nominalizations, passive voice, " +
                "corporate and academic buzzwords - so that it becomes harder to read " +
                "and understand. Do not change the underlying claims. " +
                "Return ONLY the rewritten text - no quotes, no explanations, no markdown fences.",
            "lengthen" =>
                "You are a lengthening rewriter. Elaborate and expand the phrasing of " +
                "the provided text purely to make it longer - richer transitions, " +
                "fuller sentences, more restatement - WITHOUT adding any new facts, " +
                "substance, examples or ideas that are not already there. " +
                "Return ONLY the rewritten text - no quotes, no explanations, no markdown fences.",
            "shorten" =>
                "You are a condensing rewriter. Rewrite the provided text ruthlessly " +
                "condensed to its core meaning - eliminate every trace of fluff, filler " +
                "and repetition. Keep every surviving claim accurate. " +
                "Return ONLY the rewritten text - no quotes, no explanations, no markdown fences.",
            _ =>
                "You are a humanizing rewriter. Rewrite the provided text so it reads as " +
                "naturally, quietly human prose. Vary sentence length hard - mix short " +
                "three-word punches with longer, unhurried sentences (high burstiness). " +
                "These words and phrases are BANNED: delve, testament, tapestry, crucial, " +
                "pivotal, foster, intertwined, multifaceted, underscores, moreover, beacon, " +
                "furthermore, in conclusion. Break up three-part parallelisms. Prefer " +
                "natural idioms and everyday contractions (it's, don't, can't). Keep the " +
                "meaning exactly. " +
                "Return ONLY the rewritten text - no quotes, no explanations, no markdown fences.",
        };

        // The rewrite stream rides one shared client: the engine's own
        // connection pool, no per-call setup, the body read as it lands.
        private static readonly HttpClient AiStreamHttp = CreateAiStreamClient();

        private static HttpClient CreateAiStreamClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(180);
            return client;
        }

        /// <summary>The washer (v1.19.79): thinking models, markdown fences,
        /// JSON envelopes, escaped quotes and wrapped quotation marks all
        /// come off before a rewrite touches the page.</summary>
        private static string CleanAiGeneratedText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string text = raw.Trim();

            // Strip reasoning tags (<think>...the closing tag) from thinking models
            int thinkStart = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (thinkStart >= 0)
            {
                int thinkEnd = text.IndexOf("<" + "/think>", thinkStart, StringComparison.OrdinalIgnoreCase);
                if (thinkEnd >= 0)
                    text = (text[..thinkStart] + text[(thinkEnd + 8)..]).Trim();
            }

            // Strip markdown code fences if wrapped
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                int firstLine = text.IndexOf('\n');
                if (firstLine >= 0) text = text[(firstLine + 1)..].Trim();
                if (text.EndsWith("```", StringComparison.Ordinal))
                    text = text[..^3].Trim();
            }

            // Parse JSON if output was emitted as a JSON object
            if (text.StartsWith('{') && text.EndsWith('}'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    string[] candidateProps = { "rewritten_text", "rewrittenText", "text", "result", "corrected_text", "output", "content" };
                    foreach (var prop in candidateProps)
                    {
                        if (root.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
                        {
                            text = val.GetString() ?? string.Empty;
                            break;
                        }
                    }
                }
                catch { /* fallback to string parsing */ }
            }

            // Unescape literal escaped quotes: backslash+quote -> quote
            string bsq = "\\" + "\"";
            if (text.Contains(bsq, StringComparison.Ordinal))
            {
                text = text.Replace(bsq, "\"");
            }

            // Strip wrapping outer quotes if the entire text was encapsulated in quotes
            if ((text.StartsWith('\"') && text.EndsWith('\"') && text.Length >= 2) ||
                (text.StartsWith('\u201C') && text.EndsWith('\u201D') && text.Length >= 2))
            {
                text = text[1..^1].Trim();
            }

            return text.Trim();
        }

        /// <summary>The streaming path (v1.19.79): one OpenAI-compatible
        /// request wearing stream:true, its SSE deltas posted to the sheet as
        /// they land. Returns null when the stream never carried a single
        /// delta - the one-shot provider call answers instead; non-null once
        /// the page has been told the story one way or the other.</summary>
        private async System.Threading.Tasks.Task<string?> TryStreamAiSelectionAsync(
            int seq, string system, string text,
            Features.AI.AiProviderConfig config, string kind)
        {
            bool started = false;
            try
            {
                var body = new Dictionary<string, object?>
                {
                    ["model"] = config.Model,
                    ["messages"] = new List<object>
                    {
                        new { role = "system", content = system },
                        new { role = "user", content = text }
                    },
                    ["temperature"] = config.Temperature,
                    ["max_tokens"] = config.MaxTokens,
                    ["top_p"] = config.TopP,
                    ["stream"] = true,
                };
                if (!string.IsNullOrEmpty(config.ReasoningEffort))
                    body["reasoning_effort"] = config.ReasoningEffort;
                if (config.Model.Contains("nemotron", StringComparison.OrdinalIgnoreCase))
                    body["chat_template_kwargs"] = new { enable_thinking = true };

                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"{config.BaseUrl.TrimEnd('/')}/chat/completions");
                request.Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer",
                    string.IsNullOrEmpty(config.ApiKey) ? "none" : config.ApiKey);

                using var response = await AiStreamHttp.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) return null;
                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                var raw = new StringBuilder();
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string payload = line["data:".Length..].Trim();
                    if (payload == "[DONE]") break;
                    string? delta = null;
                    try
                    {
                        using var frame = JsonDocument.Parse(payload);
                        var choices = frame.RootElement.GetProperty("choices");
                        if (choices.GetArrayLength() > 0
                            && choices[0].TryGetProperty("delta", out var d)
                            && d.TryGetProperty("content", out var c)
                            && c.ValueKind == JsonValueKind.String)
                            delta = c.GetString();
                    }
                    catch
                    {
                        // an exotic or partial frame rides past; content frames do the talking
                    }
                    if (string.IsNullOrEmpty(delta)) continue;
                    raw.Append(delta);
                    started = true;
                    Post(new { cmd = "aiStreamDelta", seq, delta });
                }

                if (!started) return null;   // nothing streamed: the one-shot path answers

                // The raw run is judged on its cleaned self: fences, JSON
                // wrappers and escaped quotes never survive the done message.
                string cleaned = CleanAiGeneratedText(raw.ToString());
                string? finalText = cleaned;
                string? message = (string?)null;
                bool ok = true;
                if (kind == "fix" && string.Equals(cleaned, text.Trim(), StringComparison.Ordinal))
                {
                    finalText = null;
                    message = "Grammar looks good!";
                }
                else if (cleaned.Length == 0)
                {
                    ok = false;
                    finalText = null;
                    message = "The model came back empty - nothing changed.";
                }
                Post(new { cmd = "aiStreamDone", seq, ok, text = finalText, message });
                return cleaned;
            }
            catch (Exception ex)
            {
                if (!started) return null;   // the stream never spoke: fall back to one-shot
                Post(new { cmd = "aiStreamDone", seq, ok = false, text = (string?)null,
                           message = "AI request failed: " + ex.Message });
                return string.Empty;         // the page has been told; the story ends here
            }
        }

        private async System.Threading.Tasks.Task RunAiSelectionAsync(
            int seq, string text, string style, string kind)
        {
            try
            {
                if (text.Length > 16000) text = text[..16000];
                string system = kind == "rewrite"
                    ? AiRewriteSystem(string.IsNullOrWhiteSpace(style) ? "humanize" : style)
                    : "You are an expert copyeditor. Fix every grammar, spelling and " +
                      "punctuation mistake in the provided text and polish multi-sentence " +
                      "structure and flow. Preserve the author's meaning and voice. " +
                      "If the text is already correct, return it unchanged. " +
                      "Return ONLY the corrected text - no quotes, no explanations, no markdown fences.";
                var config = AiConfig(Features.AI.AiSurface.EditorRewrite);
                // The loud door streams (v1.19.79): an OpenAI-compatible
                // endpoint answers chunk by chunk and the sheet types them out
                // at its own 20ms cadence; the one-shot call below stays as the
                // fallback for a stream that never carried a single delta.
                string? streamed = await TryStreamAiSelectionAsync(seq, system, text, config, kind);
                if (streamed is not null) return;

                var answer = await Features.AI.AiProviderFactory.CreateProvider(config.ProviderType)
                    .GetChatCompletionAsync(
                        system,
                        new List<Features.AI.ChatMessage>
                        {
                            new Features.AI.ChatMessage
                            {
                                MessageRole = Features.AI.ChatMessage.Role.User,
                                Content = text
                            }
                        },
                        new List<Features.AI.DocumentChunk>(),
                        "",
                        config,
                        System.Threading.CancellationToken.None);
                // Fences, JSON wrappers, escaped quotes and thinking traces
                // all come off before the answer rides to the page (v1.19.79).
                string outText = CleanAiGeneratedText(answer.Answer);
                if (kind == "fix" && string.Equals(outText, text.Trim(), StringComparison.Ordinal))
                {
                    // Nothing came back but the same words: the grammar was
                    // fine, and the page whispers so instead of changing.
                    Post(new { cmd = "aiResult", seq, kind, ok = true,
                               text = (string?)null, message = "Grammar looks good!" });
                }
                else if (outText.Length == 0)
                {
                    Post(new { cmd = "aiResult", seq, kind, ok = false,
                               text = (string?)null,
                               message = "The model came back empty - nothing changed." });
                }
                else
                {
                    Post(new { cmd = "aiResult", seq, kind, ok = true,
                               text = outText, message = (string?)null });
                }
            }
            catch (Exception ex)
            {
                Post(new { cmd = "aiResult", seq, kind, ok = false,
                           text = (string?)null,
                           message = "AI request failed: " + ex.Message });
            }
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
