# TASK: Fix Broken Browser PDF Porting & Add Fresh "New Tab" (+) Button

Repository: `https://github.com/etb190/Avalanche`  
Target Files: `Controls/WebBrowserControl.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`  
Version: `1.19.6` (or next patch)

---

## 1. Issue 1: Fix Broken PDF Porting ("Doesn't port, only 1 PDF worked")

### Problem Diagnosis in `WebBrowserControl.xaml.cs`:
In commit `ff4acf3`, early response interception was added, but it introduced a **fatal race condition and redirect disconnect** that broke PDF handoffs completely:

1. **Race Condition in `OnWebResourceResponseReceived`**:
   - Lines 721–727:
     ```csharp
     if (LooksLikePdf(uri) || UriEquals(uri, _mainNavUri))
         StartEarlyPdfHandoff(uri);               // 1. Spawns asynchronous handoff worker
     Stream? content = await e.Response.GetContentAsync();
     ...
     TaskCompletionSource<byte[]?> capture = new(...);
     _pdfCapture = capture;                       // 2. Assigns _pdfCapture AFTER starting worker!
     ```
   - In `StartEarlyPdfHandoff(url)`, line 393:
     ```csharp
     byte[]? bytes = await WaitPdfCaptureAsync(TimeSpan.FromSeconds(20));
     ```
   - Because `StartEarlyPdfHandoff` runs before `_pdfCapture` is assigned, `WaitPdfCaptureAsync` immediately sees `_pdfCapture == null` and returns `null`.
   - Line 405 runs:
     ```csharp
     _pdfInFlight.Remove(url);
     DropVisualGuard(restore: true);
     ```
   - The handoff terminates prematurely with failure, drops the visual guard, and leaves the user stranded on Chromium's viewer or in limbo.
2. **Redirect Disconnect with `_mainNavUri`**:
   - `_mainNavUri` was recorded only in `OnNavigationStarting`.
   - When a link redirects (e.g. `scholar.google.com/url?...` -> `sciencedirect.com/article/...` -> `cdn.sciencedirect.com/.../main.pdf`), `e.Request.Uri` in `OnWebResourceResponseReceived` does NOT match the initial `_mainNavUri`.
   - Unless the final redirected URL ends strictly in `.pdf`, `UriEquals(uri, _mainNavUri)` is false, so `StartEarlyPdfHandoff` never even gets called.
3. **Dead Fallback in `CatchInlinePdfAsync`**:
   - After the early handoff aborts or fails due to the race condition, `NavigationCompleted` triggers `CatchInlinePdfAsync()`, but the internal flags/state prevent a clean recovery.

### Required Architectural Fix:
1. **Eliminate the Race Condition in `OnWebResourceResponseReceived`**:
   - Create and assign `TaskCompletionSource<byte[]?>` **BEFORE** calling `StartEarlyPdfHandoff(uri)`.
   - Read the response stream asynchronously, populate the byte array, and set `capture.TrySetResult(bytes)`.
   - `StartEarlyPdfHandoff` will then reliably await and receive the full valid PDF bytes every time.
2. **Handle Redirects Accurately**:
   - Update `_mainNavUri` whenever navigation progresses or redirects occur, or check if the response `Content-Type` is `application/pdf` or `application/x-pdf` regardless of URL matching when it is a top-level document response.
3. **Bulletproof Fallback in `CatchInlinePdfAsync`**:
   - If early interception is missed or not triggered, `CatchInlinePdfAsync` in `NavigationCompleted` must execute its multi-layer capture:
     1. Byte capture from the live engine stream.
     2. In-page authenticated `fetch(location.href)` script.
     3. Session-credentialed out-of-process fetch.
   - If `%PDF-` header is verified, hand it to `PdfRequested` and step the browser back cleanly.

---

## 2. Issue 2: Add Fresh "New Tab" (+) Button to the Browser Tab Strip

### The Problem:
In `MainWindow.xaml`, the browser tab strip (`WebPaneHost`) only displays a single static tab:
`[ 🌐 New tab      ✕ ]`
There is no `+` (New Tab) button. The reader cannot open a fresh tab or easily start a new search without manually erasing the address bar or closing the entire browser pane.

### Required Architecture & Fix:
1. **Add `+` (New Tab) Button in `MainWindow.xaml`**:
   - In `WebPaneHost`'s tab band (row 0), right next to `WebTab`:
     ```xaml
     <Button x:Name="WebNewTabBtn" Content="&#xE710;"
             Style="{DynamicResource TabNewButton}"
             FontFamily="{DynamicResource IconFont}" FontSize="11"
             Width="24" Height="22" Margin="4,0,0,0" VerticalAlignment="Center"
             ToolTip="{DynamicResource Str_TT_WebNewTab}"
             Click="WebNewTabBtn_Click"/>
     ```
2. **New Tab Click Behavior in `MainWindow.xaml.cs`**:
   - Add click handler `WebNewTabBtn_Click`:
     - Creates a fresh tab entry in the browser's tab collection (or navigates the browser to `HomePage` / DuckDuckGo).
     - Clears the omnibox and focuses it immediately so the user can type a search query or URL right away.
     - Adds a card to the sidebar's Web Tabs gallery (`WebPane.Tabs`), ensuring the new tab is visible and switchable.
3. **Localization**:
   - Add `Str_TT_WebNewTab` ("New Tab (Ctrl+T)") to `Strings/en-US.xaml` and all sister locale resource dictionaries.

---

## 3. Verification Checklist

1. **PDF Porting Verification**:
   - Test 10 different PDF links across various websites (arXiv, Google Scholar, ScienceDirect, PubMed, Springer, direct .pdf links, and tokenized redirect links).
   - Verify that all valid PDFs are successfully ported into Avalanche reader tabs with high reliability (10/10).
   - Verify that Chromium's internal viewer does not linger or freeze.
2. **New Tab (+) Button Verification**:
   - Open browser mode in Avalanche.
   - Verify the `+` button appears directly next to the active web tab in the tab strip.
   - Click the `+` button; verify a fresh tab opens, omnibox is cleared and focused, and the page is ready for browsing.
3. **Test Suite**:
   - Run `dotnet test` to verify all existing unit tests and localization key parity pass without errors.
