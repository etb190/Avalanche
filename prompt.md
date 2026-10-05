# TASK: Fix Persistent Browser PDF Viewer Leaks, Infinite "Fetching" Status, and Tab Strip / Gallery Sync

Repository: `https://github.com/etb190/Avalanche`  
Target Files: `Controls/WebBrowserControl.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`  
Version: `1.19.7` (or next patch)

---

## 1. Overview of Remaining Critical Issues

Users are testing real-world academic and journal PDF links and encountering three specific problems:

### A. The Links Tested:
1. **Working (delayed or instant)**:
   - `https://www.jstor.org/stable/pdf/786314.pdf`
   - `https://www.jstor.org/stable/pdf/789704.pdf`
   - `https://www.academia.edu/download/124500573/1243.pdf`
   - `https://www.stevehedley.com/odg/attachments/NEYERS_(Theory_of_VL).pdf`
   - `https://papers.ssrn.com/sol3/Delivery.cfm?abstractid=2667080`
   - `https://papers.ssrn.com/sol3/Delivery.cfm?abstractid=2432094`
2. **Failed: Still opens in Chromium's internal PDF viewer (or link clicks do nothing)**:
   - `https://chicagounbound.uchicago.edu/cgi/viewcontent.cgi?article=13702&context=journal_articles`
   - `https://onlinelibrary.wiley.com/doi/pdf/10.1111/j.1468-2230.1957.tb00440.x`
   - `https://www.researchgate.net/profile/Paula-Giliker/publication/272264532_Vicarious_Liability_or_Liability_for_the_Acts_of_Others_in_Tort_A_Comparative_Perspective/links/595f9197458515a357b3ee0b/Vicarious-Liability-or-Liability-for-the-Acts-of-Others-in-Tort-A-Comparative-Perspective.pdf`
   - `https://www.repository.law.indiana.edu/cgi/viewcontent.cgi?article=11519&context=ilj`
   - `https://ajronline.org/doi/pdf/10.2214/ajr.169.3.9275865`

---

## 2. Root Cause Analysis

### Issue 1: Why those specific links open in Chromium's viewer or hang forever with "Fetching the PDF..."
1. **The `Sec-Fetch-Dest` & Sub-frame trap in `OnWebResourceResponseReceived`**:
   - In `WebBrowserControl.xaml.cs`:
     ```csharp
     string dest = e.Request.Headers.Contains("Sec-Fetch-Dest") ? ...;
     bool mainDocument = dest.Equals("document", StringComparison.OrdinalIgnoreCase);
     ...
     if (dest.Length > 0 && !mainDocument) return;
     ```
   - Sites like Wiley, ResearchGate, Chicago Unbound, Indiana Law Repository, and AJR deliver PDFs via:
     - Cross-origin CDN redirects where `Sec-Fetch-Dest` is `empty` or not sent as `"document"`.
     - In-page viewer plugins or embedded `<embed>` / `<object>` wrappers where `dest` is `"embed"` or `"object"` rather than `"document"`.
     - Because `dest.Length > 0 && !mainDocument` evaluates to `true`, the response filter returns immediately!
     - It completely skips capturing the stream!
2. **The Infinite "Fetching the PDF..." Status**:
   - `ShowStatus(TryLoc("Str_Web_PdfOpening"))` is displayed when `ArmVisualGuard()` or `StartEarlyPdfHandoff` is triggered.
   - If the capture times out or fails (or if the response is ignored due to the `dest` filter above), `HideStatus()` is **never called** in several code paths (such as when `WaitPdfCaptureAsync` hangs waiting for a capture that was dropped, or when fallback methods silently return `null`).
   - The reader is left staring at *"Fetching the PDF for the reader..."* permanently.
3. **In-Page JavaScript Fetch Fails on Plugin / Viewer DOMs (`PageFetchScript`)**:
   - When Chromium renders a PDF, the page DOM is not an HTML page with JavaScript `fetch()`: it is Chromium's internal PDF viewer extension (`chrome-extension://...` or `<embed type="application/pdf">`).
   - Running `await core.ExecuteScriptAsync(PageFetchScript)` inside a native PDF extension either returns an empty string or throws a security exception (`SecurityError: Failed to fetch`).
4. **Out-of-Process `FetchBrowserBytesAsync` Cookie Path Failure**:
   - Adding cookies from WebView2 via `handler.CookieContainer.Add(target, new Cookie(...))` fails if `c.Domain` contains leading dots or port numbers that .NET's strict `Cookie` constructor rejects, throwing `CookieException` inside the loop and aborting the request.

---

## 3. Required Architectural Solutions

### A. Disable Chromium's PDF Viewer in WebView2 Settings (The Core Fix)
The user noted:
> *"Some browsers don't even preview pdfs, they only allow you to preview downloaded pdfs so if you try to click a pdf it downloads it, maybe doing that fixes that?"*

**YES!** If we configure WebView2 to treat PDFs as downloads rather than rendering them internally, Chromium will **never** display its built-in viewer, and every PDF click will stream natively into `OnDownloadStarting`!
- In `EnsureReadyAsync()`:
  - Configure `core.Settings.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.None;`
  - Pass browser argument to disable internal PDF viewer plugin if applicable, OR:
  - If a top-level response has `Content-Type: application/pdf`, **intercept regardless of `Sec-Fetch-Dest`**:
    ```csharp
    // Do NOT discard responses where dest != "document"!
    // If the mime type is application/pdf, capture it!
    ```

### B. Guarantee "Fetching PDF" Status Strip Cleans Up:
- Wrap all handoff routines (`StartEarlyPdfHandoff`, `CatchInlinePdfAsync`, `OpenRemotePdfAsync`) in a `finally` block that **always** calls `HideStatus()`:
  ```csharp
  try { ... }
  finally { HideStatus(); }
  ```
- If the fetch fails, display a clear, brief status message (e.g. `Str_Web_PdfBlocked`), then hide it after 3 seconds so the status bar does not get stuck.

### C. Fallback for Embedded / Viewer DOMs:
- If Chromium renders a PDF page, do not rely on `document.contentType == "application/pdf"`.
- Use `core.PrintToPdfAsync` or download the active URL directly using the WebView2 native download API (`core.DownloadStarting`), which bypasses DOM restrictions.

---

## 4. Issue 2: Sidebar Gallery Order & Top Tab Strip Sync

### The Problem:
1. **Sidebar Gallery is inverted**:
   - The active/newest tab appears at the bottom.
   - The user requested: **"from newest to oldest from top to bottom"**.
2. **Top Tab Strip only has one tab**:
   - The top tab strip in `MainWindow.xaml` (`WebPaneHost`) only shows the single active tab.
   - The user wants the top tab strip to populate with all open tabs just like the PDF viewer's tab strip, allowing the reader to switch between web tabs from the top strip as well as the sidebar.

### Required Architecture & Fix:
1. **Reverse Sidebar Gallery Order (Newest on Top)**:
   - When adding a new or revisited card in `Tabs`:
     - Use `Tabs.Insert(0, card)` instead of `Tabs.Add(card)`.
     - When moving a revisited card, remove it and insert it at index `0`.
     - This ensures the newest/active tab is always at the **top** of the left sidebar gallery, followed by older tabs below it.
2. **Populate Top Tab Strip with Web Tabs**:
   - In `MainWindow.xaml`, replace the single static `WebTab` with an `ItemsControl` (or dynamic tabs panel) bound to `WebPane.Tabs` (or synchronized list):
     - Each tab in the top strip shows the tab title, globe icon, active face, and close button (`✕`).
     - Clicking a tab in the top strip activates that tab (`WebPane.ActivateTab(tab.Url)`).
     - The `+` (New Tab) button sits at the end of the top tab row.

---

## 5. Verification Checklist

1. **Test the Previously Failing Links**:
   - Test `chicagounbound.uchicago.edu`
   - Test `onlinelibrary.wiley.com`
   - Test `researchgate.net`
   - Test `repository.law.indiana.edu`
   - Test `ajronline.org`
   - Verify all 5 links now successfully hand over to Avalanche without getting trapped in Chromium's viewer or clicking doing nothing.
2. **Status Bar Clean-up**:
   - Verify "Fetching the PDF for the reader..." never gets stuck on screen.
3. **Sidebar Gallery Ordering**:
   - Open multiple tabs (e.g. Google Scholar, JSTOR, arXiv).
   - Verify the newest / currently active tab is at the **top** of the left rail.
4. **Top Tab Strip Multi-Tab Support**:
   - Verify the top tab strip displays all open web tabs side-by-side with close chips and the `+` button.
