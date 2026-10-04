# TASK: Lightweight In-App Browser Integration via Microsoft WebView2

Repository: `https://github.com/etb190/Avalanche`  
Target Version: `1.19.0` (or next minor)

---

## 1. Overview & Architectural Goals

The user wants to browse the web inside Avalanche to:
1. **Search & explore books, research papers, and documents** directly from the web without leaving the app.
2. **Seamlessly open PDFs discovered on the web in Avalanche's native editor** (with instant access to AI summaries, Axo notes sync, annotations, highlights, etc.).
3. **Have curated quick-access website icons** (e.g. Internet Archive, Google Scholar, PubMed, Project Gutenberg, arXiv, Sci-Hub, Library Genesis, etc.) accessible within the app.
4. **Keep build size and resource footprint minimal**:
   - Do **NOT** bundle Chromium or CEF (which would bloat installer/portable binaries by 150–250MB+).
   - Use Windows' system-provided **Microsoft WebView2 Evergreen Runtime** (`Microsoft.Web.WebView2`).
   - Add < 1 MB to the binary distribution.
   - Maintain 0% memory and CPU overhead when the browser is not in use (lazy loading + process suspension).

---

## 2. Core Technical Strategy: Microsoft WebView2 (Evergreen)

### Why WebView2?
- **Zero Heavy Binaries Bundled**: Windows 10 (modern builds) and Windows 11 ship with the WebView2 Evergreen runtime pre-installed at the OS level. Avalanche only needs the managed NuGet package `Microsoft.Web.WebView2` (~400 KB - 1 MB managed wrapper).
- **Security & Updates**: OS-managed security patches; no need to recompile Avalanche when Chromium releases security updates.
- **WPF Native Support**: Provides `<wv2:WebView2 />` controls that blend directly into WPF layouts.
- **Resource Management**: When browser tabs or panes are collapsed/hidden, WebView2 supports `CoreWebView2.TrySuspendAsync()` to release RAM and GPU surfaces back to Windows.

---

## 3. Detailed Specifications

### A. Navigation & UI Integration
1. **Access Point**:
   - A browser button / tab toggle in the top toolbar or sidebar (e.g. Globe icon `\uE774` in Segoe MDL2 Assets).
   - Alternatively, support opening Web Tabs alongside document tabs in the tab strip (`TabStrip.xaml` / `ActiveViewer`), or as a dedicated split pane / modal viewer.
2. **Browser Chrome / Navigation Bar**:
   - **Back** (`\uE72B`), **Forward** (`\uE72A`), **Refresh** (`\uE72C`), **Home** (`\uE80F`).
   - **URL / Search Omnibox**: Accepts full URLs or search queries (defaulting to Google/DuckDuckGo or academic search).
   - **Quick-Access Speed Dial / Bookmark Bar**:
     - Curated academic/book discovery hubs as compact icon chips:
       - **Internet Archive** (`archive.org`)
       - **Google Scholar** (`scholar.google.com`)
       - **Project Gutenberg** (`gutenberg.org`)
       - **arXiv** (`arxiv.org`)
       - **PubMed** (`pubmed.ncbi.nlm.nih.gov`)
     - Clicking any chip navigates the active WebView directly.
3. **"Open in Avalanche" Action Bar Button**:
   - An icon button: **"Convert Web Page to PDF & Open"**.
   - Uses `CoreWebView2.PrintToPdfAsync` to capture the current webpage into a temporary PDF and immediately loads it into Avalanche for reading and highlighting.

### B. Intelligent PDF Interception & Handoff
When the user clicks a PDF link or initiates a PDF download within the browser:
1. **Intercept Download**:
   - Subscribe to `CoreWebView2.DownloadStarting`:
     ```csharp
     webView.CoreWebView2.DownloadStarting += (sender, args) =>
     {
         string mime = args.DownloadOperation.MimeType ?? "";
         string path = args.ResultFilePath ?? "";
         if (mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
         {
             args.Handled = true; // suppress default Edge download flyout
             string tempTarget = Path.Combine(Path.GetTempPath(), "Avalanche", "WebDownloads", Path.GetFileName(path));
             args.ResultFilePath = tempTarget;
             
             args.DownloadOperation.StateChanged += (s, e) =>
             {
                 if (args.DownloadOperation.State == CoreWebView2DownloadState.Completed)
                 {
                     Dispatcher.Invoke(() => ActiveViewer.OpenInNewTabExt(tempTarget));
                 }
             };
         }
     };
     ```
2. **Intercept Navigation to Direct `.pdf` URLs**:
   - In `CoreWebView2.NavigationStarting`, check if `args.Uri` ends with `.pdf`. If so, cancel navigation, download the stream via `HttpClient` or background downloader, and open in Avalanche's native PDF canvas instead of Edge's built-in PDF viewer.

### C. Resource Efficiency & Lazy Loading
1. **Lazy Initialization**:
   - Do NOT call `EnsureCoreWebView2Async()` during app startup.
   - Initialize WebView2 only when the user first opens the browser tab or panel.
2. **Suspension on Background/Hide**:
   - When the user switches to a PDF tab or minimizes the browser pane:
     - Call `CoreWebView2.TrySuspendAsync()` to drastically reduce memory and halt background timers/scripts.
   - On tab switch back:
     - Call `CoreWebView2.Resume()`.
3. **Dedicated User Data Folder**:
   - Store WebView2 browser cache/cookies under `%LOCALAPPDATA%\Avalanche\WebView2Data` to keep it isolated and cleanable.

---

---

## 5. Keyboard Scrolling Tuning: Slow & Smooth Arrow / W / S Navigation (Without Affecting Mouse)

### The Problem
Scrolling with the keyboard (Up/Down arrow keys, and W/S in navigator/viewer) currently feels **too fast, abrupt, or jumpy**:
1. **Main Document Viewer (`Shell/KeyboardShortcuts.cs`)**:
   - `ArrowScrollStep` is set to `48.0` px, and pressing Up/Down calls `PagePreviewPanel.ScrollToVerticalOffset(...)` instantly with no smooth deceleration/easing curve.
   - Rapid key repeats cause the viewport to jerk or leap down the page aggressively.
2. **Summary Navigator (`Features/Summary/SummaryWindow.xaml.cs`)**:
   - W/S and Up/Down scroll jump by `Math.Max(viewport * 0.85, 40.0)` — scrolling almost a full page (`85%` of the viewport) per press!
   - This makes reading dense digest text frustrating because pressing S or Down jumps past several paragraphs at once.

### Required Architecture & Fix:
1. **Scope - Keyboard Only (Preserve Mouse Wheel)**:
   - **Do NOT modify mouse wheel handlers** (`PreviewMouseWheel`, `NavigatePageByWheel`). Mouse wheel physics/steps must remain completely unchanged.
2. **Document Viewer Smooth & Gentle Keyboard Scroll**:
   - Introduce smooth animated scroll easing or smaller, gentler steps for keyboard navigation:
     - Reduce immediate discrete step or animate `VerticalOffset` with a subtle ease-out animation (e.g. ~180–220ms `CubicEase` or `QuadraticEase`).
     - Tune step distance to a comfortable reading cadence (e.g. ~24–32 px per step or ~12–15% viewport line step, rather than 48+ px raw jumps).
   - Support W and S keys if mapped, matching Up and Down behavior.
3. **Summary Navigator W / S / Up / Down Refinement**:
   - Reduce the target delta in `SmoothScrollDigest`:
     - Change from `viewport * 0.85` (which skips almost the whole screen) to a much gentler reading step (e.g. `viewport * 0.25` or `~60–80 px`).
     - Retain `CubicEase` easing so continuous taps smoothly chain without jumping.

---

## 6. Implementation Steps for the AI Developer

1. **Project Dependencies**:
   - Add `Microsoft.Web.WebView2` NuGet package to `Avalanche.csproj`.
   - Update `build/payload-files.txt` and packaging scripts to include the managed `Microsoft.Web.WebView2.Core.dll` and runtime loader (note: `WebView2Loader.dll` is small ~160KB).
2. **Create Browser Component**:
   - Implement `Controls/WebBrowserControl.xaml` and `Controls/WebBrowserControl.xaml.cs`.
   - Create UI with Address Bar, Back/Forward/Reload buttons, Quick-Access site bar, and `<wv2:WebView2 x:Name="Browser" />`.
3. **Implement PDF Handlers**:
   - Wire `DownloadStarting` and `NavigationStarting` to detect PDF links.
   - Implement temporary storage and forward to `MainWindow.ActiveViewer.OpenInNewTabExt`.
4. **Tune Keyboard Scrolling**:
   - Update `Shell/KeyboardShortcuts.cs` and `Features/Summary/SummaryWindow.xaml.cs` to make keyboard scrolling (Arrows + W/S) slow, gentle, and smooth, leaving mouse wheel untouched.
5. **Localization**:
   - Add all button tooltips and labels to `Strings/en-US.xaml` and sister locale resource dictionaries.
6. **Testing & Verification**:
   - Build single-executable / portable payload (`build\build-portable.ps1`).
   - Verify executable size did NOT increase by more than ~1-2 MB.
   - Verify launching and browsing works seamlessly without lagging PDF rendering.
   - Verify clicking a PDF link on arXiv / Project Gutenberg opens directly inside Avalanche's viewer.
   - Verify Up/Down and W/S keys scroll smoothly and gently, while mouse wheel behaves normally.
