# TASK: Browser PDF Fast Interception, AI Semantic Toggle, Browser Tabs Gallery, & Web PDF Save Button

Repository: `https://github.com/etb190/Avalanche`  
Target Version: `1.19.5` (or next release)

---

## 1. Overview of Tasks

This update addresses four key areas in Avalanche:
1. **Early PDF Interception in In-App Browser**: Eliminate the flash/glimpse of Chromium's internal PDF viewer before handoff occurs.
2. **AI Chat Semantic Research Manual Toggle**: Stop automatic, compute-heavy embedding passes when quickly browsing papers by adding an opt-in toggle button in the AI Chat header.
3. **Sidebar Left Panel in Browser Mode**: When the browser is active, replace document page thumbnails with live tab preview screenshots/cards, allowing clicking to switch tabs.
4. **"Save Web PDF to Disk" Toolbar Action**: When viewing a PDF downloaded from the browser (stored in temp), display a prominent "Save to Disk" / "Save As" button right after the browser tools on the toolbar so users can pick where to save it permanently.

---

## 2. Issue 1: Prevent Delayed Interception / Flash of Chromium's PDF Viewer

### The Problem
When a user clicks a PDF link, `NavigationCompleted` triggers `CatchInlinePdfAsync()`. Because this waits until the full document finishes rendering in Chromium, the user briefly sees the Edge/Chromium PDF viewer UI before Avalanche yanks it out and opens it in the reader tab.

### Required Architecture & Fix
1. **Intercept at `WebResourceResponseReceived` or `NavigationStarting`**:
   - `core.WebResourceResponseReceived` fires as soon as the HTTP response headers arrive (`Response.Headers`).
   - If the main frame response has `Content-Type: application/pdf` or `application/x-pdf`:
     - Immediately start the handoff or redirect.
2. **Early Visual Guard**:
   - In `NavigationStarting`, if `LooksLikePdf(uri)` is true, or once response headers indicate a PDF, immediately blank or hide the WebView2 frame (or show the "Opening PDF in reader..." status plate) so Chromium's PDF viewer canvas is never painted to the screen.
3. **Streamlined Capture**:
   - Use the already-intercepted response body stream from `WebResourceResponseReceived` or `DownloadStarting` instead of waiting for full DOM completion in `NavigationCompleted`.

---

## 3. Issue 2: AI Chat Manual "Semantic Research" Button

### The Problem
When rapidly opening PDFs and research papers from the browser, `AiChatViewModel.InitializeForDocumentAsync()` automatically kicks off background Ollama/embedding passes (`SemanticStatus = _loc("Str_AiChatSemanticBuilding")`). Running embeddings on every freshly opened paper burns massive CPU/GPU compute and causes unnecessary fan spin.

### Required Architecture & Fix
1. **Make Semantic Research Opt-In**:
   - In `AiChatViewModel.cs`, disable automatic background embedding during document initialization.
   - Text extraction, chunking, and BM25 keyword search remain instant and automatic.
   - The semantic vector embedding pass only runs when the reader explicitly clicks the button.
2. **UI Button in AI Chat Header (`MainWindow.xaml`)**:
   - In the `AiChatOverlay` header row (currently containing `AiChatSettingsBtn`, `AiChatNewChatBtn`, `AiChatCloseBtn`):
   - Add a new button **immediately to the left of `AiChatSettingsBtn`** (Column 2 or inserted before settings):
     - Size: `24x24`, `Padding="0"`, `FontSize="10"`, `Style="{StaticResource ToolbarButton}"`.
     - Icon: Segoe MDL2 icon representing research / embeddings / spark / brain (e.g. `\uE946` Sparkle, `\uF1AD` Research/Search, or `\uE773` Knowledge).
     - ToolTip: DynamicResource `Str_TT_AiSemanticResearch` ("Build Semantic Research Index").
     - Visual state: Accent color when semantic index is ready or building; muted when idle.
3. **Click Behavior**:
   - When clicked, if semantic index is not built, trigger `BuildSemanticIndexAsync()`.
   - If already building or ready, provide clear feedback (or toggle off/cancel).

---

## 4. Issue 3: Left Sidebar in Browser Mode — Web Tabs Gallery

### The Problem
When the browser view (`WebPaneHost`) is visible, the left sidebar still shows the thumbnail pages of the previously opened PDF document (`PageList`), which is completely disconnected from the active web session.

### Required Architecture & Fix
1. **Sidebar State Awareness**:
   - When `ShowWebPane()` is called, switch the left sidebar content from `PageList` to a new `WebTabsHost` (or dedicated Web Tabs list panel).
   - When `HideWebPane()` is called, restore `PageList`.
2. **Tab Previews & Selection**:
   - Display a vertical list of cards representing the browser tabs (or history/open views, e.g. Google Scholar, NotebookLM, arXiv):
     - Each card shows a live or captured screenshot thumbnail of the page, the page title, and the site favicon/host.
     - Sorted in tab order (active tab highlighted with theme border/accent).
   - **Clicking a card**: Switches focus directly to that web tab/URL in the browser, matching the behavior of clicking the tab strip.
3. **Capturing Thumbnails**:
   - Use `CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)` on page load / navigation complete to keep thumbnail images updated for each tab.

---

## 5. Issue 4: "Save to Disk" Button for Web-Opened PDFs

### The Problem
When PDFs are opened from the browser, they live in a temporary cache (`AppData\Local\Temp\Avalanche\WebDownloads\...`). There is currently no prominent, obvious way for users to save the document to their Documents or Books folder without hunting for Save As in dropdown menus.

### Required Architecture & Fix
1. **Detect Web-Downloaded PDF**:
   - Track whether the active tab's file was created by the in-app browser (e.g., `_isWebDownloadedPdf` or checking if `_currentFile` starts with the `WebDownloads` temp directory).
2. **Toolbar Button Placement**:
   - On the top toolbar, place a dedicated **"Save Web PDF to Disk"** icon button:
     - Position: **Immediately after the browser tools** (`WebBrowserBtn` / `GrpWeb`).
     - Visibility: `Visible` **ONLY** when the active document is a web-downloaded PDF; `Collapsed` for all ordinary local PDFs.
     - Icon: `\uE74E` (Save As / Disk) or `\uE792` (Save/Export) with an accent highlight or download badge.
     - ToolTip: `Str_TT_SaveWebPdf` ("Save this web PDF to your computer").
3. **Click Behavior**:
   - Automatically opens Avalanche's native `SaveAs_Click` file dialog.
   - Pre-seeds the dialog with the document's cleaned title/filename and defaults to the user's Downloads or Documents directory.
   - Once saved to a permanent location, update the tab's path and hide the temporary "Save Web PDF" button.

---

## 6. Implementation Checklist & Verification

1. **Browser PDF Handoff**:
   - Click a PDF link on arXiv, Google Scholar, or direct PDF URL.
   - Verify Chromium's native PDF viewer does NOT flash or render noticeably before opening in Avalanche.
2. **AI Chat Semantic Toggle**:
   - Open several PDFs from the web quickly.
   - Verify embedding process does NOT run automatically.
   - Click the new Semantic Research button in AI Chat header; verify embeddings build on demand.
3. **Left Sidebar in Browser**:
   - Toggle to Browser mode.
   - Verify sidebar displays web tab cards with previews instead of PDF page thumbnails.
   - Click a tab card; verify it switches to that tab.
4. **Save Web PDF Button**:
   - Open a PDF from the browser.
   - Verify the "Save Web PDF" button appears on the toolbar next to browser tools.
   - Click it, save to disk, and verify it saves cleanly and button disappears once persisted.
5. **Regression & Parity**:
   - Run `dotnet test` and ensure all localization keys and existing features pass.
