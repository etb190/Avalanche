using System;
using System.Collections.Generic;
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

        // ── Tabs (v1.19.68): one document per tab ─────────────────────────
        private sealed class EditorTab
        {
            public string Title = "";
            public string Html = "";
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

        private readonly List<EditorTab> _tabs = [];
        private int _activeTab;
        private int? _pendingSwitchTo;   // a tab click waiting for the page's dump
        private int _worldSeq;           // which loaded world the page is showing
        private bool _syncingLists;      // a dial highlighting itself must not apply

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
            SizePopup.Closed += (_, _) => SizeBtn.IsChecked = false;
            LoadEditorTabs();
            RebuildEditorTabs();
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
                            _tabs[_activeTab].Title = title;
                            RebuildEditorTabs();
                        }
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
                    && !string.Equals(SizeText.Text, size, StringComparison.Ordinal))
                    SizeText.Text = size;
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

        private void SizeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SizeBtn.IsChecked != true) return;
            HighlightCurrentSize();
            SizePopup.IsOpen = true;
        }

        // The caret button opens the same list - the chip and the caret are two
        // separate doors to one dropdown, and neither applies a size (v1.19.68).
        private void SizeDropBtn_Click(object sender, RoutedEventArgs e)
        {
            HighlightCurrentSize();
            SizePopup.IsOpen = true;
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
            string cur = SizeText.Text;
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
                SizeText.Text = size;
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
                            _tabs.Add(new EditorTab { Title = t.Title ?? "", Html = t.Html ?? "" });
                        _activeTab = Math.Clamp(data.Active, 0, _tabs.Count - 1);
                        return;
                    }
                }
                // The v1.19.67 single-document cache migrates as the first tab.
                string old = Path.Combine(AppDataPaths.UserRoot, "texteditor", "session.html");
                if (File.Exists(old))
                    _tabs.Add(new EditorTab { Title = Loc("Str_Editor_Untitled"), Html = File.ReadAllText(old) });
            }
            catch
            {
                // The session cache never takes the editor down.
            }
            if (_tabs.Count == 0)
                _tabs.Add(new EditorTab { Title = Loc("Str_Editor_Untitled"), Html = "" });
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

        private void RebuildEditorTabs()
        {
            EditorTabsPanel.Children.Clear();
            for (int i = 0; i < _tabs.Count; i++)
                EditorTabsPanel.Children.Add(BuildEditorTabButton(i));
        }

        private System.Windows.Controls.Button BuildEditorTabButton(int index)
        {
            var tab = _tabs[index];
            bool active = index == _activeTab;

            var title = new TextBlock
            {
                Text = string.IsNullOrEmpty(tab.Title) ? Loc("Str_Editor_Untitled") : tab.Title,
                FontSize = 11.5,
                Margin = new Thickness(2, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            title.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");

            var close = new System.Windows.Controls.Button
            {
                Content = "\uE711",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 8.5,
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null,
            };
            close.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "MutedTextBrush");
            close.Click += (_, _) => CloseEditorTab(index);

            var host = new Grid();
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(title, 0);
            Grid.SetColumn(close, 1);
            host.Children.Add(title);
            host.Children.Add(close);

            var btn = new System.Windows.Controls.Button
            {
                Content = host,
                Padding = new Thickness(10, 2, 4, 2),
                Margin = new Thickness(0, 0, 6, 0),
                MinWidth = 120,
                MaxWidth = 230,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null,
            };
            btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
                active ? "PaneBrush" : "BgCanvas");
            btn.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty,
                active ? "PrimaryBrush" : "CardBorderBrush");
            btn.SetResourceReference(System.Windows.Controls.Control.FontFamilyProperty, "UiFont");
            btn.Template = EditorTabTemplate();
            btn.Click += (_, _) => RequestEditorTabSwitch(index);
            return btn;
        }

        // The tab's face: one rounded card whose ring warms to the accent on
        // hover - the page-card template the sidebar rail already wears.
        private static System.Windows.Controls.ControlTemplate EditorTabTemplate()
        {
            var face = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border), "face");
            face.SetValue(System.Windows.Controls.Border.BackgroundProperty,
                new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
            face.SetValue(System.Windows.Controls.Border.BorderBrushProperty,
                new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
            face.SetValue(System.Windows.Controls.Border.BorderThicknessProperty,
                new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BorderThicknessProperty));
            face.SetValue(System.Windows.Controls.Border.CornerRadiusProperty,
                new System.Windows.DynamicResourceExtension("ControlCornerRadius"));
            face.SetValue(System.Windows.Controls.Border.PaddingProperty,
                new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
            face.AppendChild(new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter)));
            var tpl = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button))
            {
                VisualTree = face,
            };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new System.Windows.Setter(
                System.Windows.Controls.Border.BorderBrushProperty,
                new System.Windows.DynamicResourceExtension("PrimaryBrush")) { TargetName = "face" });
            tpl.Triggers.Add(hover);
            return tpl;
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
            RebuildEditorTabs();
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
            _tabs.Add(new EditorTab { Title = Loc("Str_Editor_Untitled"), Html = "" });
            int to = _tabs.Count - 1;
            if (!_pageReady) { ActivateEditorTabNow(to); SaveSession(); return; }
            _pendingSwitchTo = to;
            Post(new { cmd = "dump" });   // the current world saves first; the switch completes on its answer
        }

        private void CloseEditorTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            bool closingActive = index == _activeTab;
            _tabs.RemoveAt(index);
            if (_tabs.Count == 0)
                _tabs.Add(new EditorTab { Title = Loc("Str_Editor_Untitled"), Html = "" });
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
                RebuildEditorTabs();
            }
            SaveSession();
        }

        // ── Host-facing surface ──────────────────────────────────────────────────

        /// <summary>The sidebar asked for a page: the editor scrolls to it.</summary>
        public void ScrollToPage(int pageNumber) => Post(new { cmd = "scroll", n = pageNumber });
    }
}
