# TASK: Fix Remaining Academic PDF Failures (Chicago Unbound, Indiana Law, Wiley) and Zombie PDF Tabs in Browser

Repository: `https://github.com/etb190/Avalanche`  
Target Files: `Controls/WebBrowserControl.xaml.cs`, `MainWindow.xaml.cs`  
Version: `1.19.8` (or next patch)

---

## 1. Status Update & Remaining Issues

In v1.19.7, PDF handoff reliability improved (e.g. ResearchGate and AJR now succeed). However:
1. **Three academic PDF links still fail to port automatically (opening in Chromium's internal PDF viewer)**:
   - `https://chicagounbound.uchicago.edu/cgi/viewcontent.cgi?article=13702&context=journal_articles`
   - `https://www.repository.law.indiana.edu/cgi/viewcontent.cgi?article=11519&context=ilj`
   - `https://onlinelibrary.wiley.com/doi/pdf/10.1111/j.1468-2230.1957.tb00440.x`
   *(Crucial Clue: When Chrome's viewer opens on these pages, clicking its built-in 'Save' button immediately triggers Avalanche's reader via `OnDownloadStarting`. The document is 100% accessible and delivered without error; only the automatic launch triggers failed to fire!)*
2. **New Issue: The PDF does not close from the browser after going to the PDF editor; it stays in the browser and bugs out**:
   - When a PDF is handed off to Avalanche's reader, the web browser pane hides, but the PDF tab is left open in `WebPane.Tabs` (top tab strip and sidebar gallery).
   - When reopening the web browser, the tab is stuck on the PDF URL or Chromium's internal viewer in a frozen/blanked state.

---

## 2. Root Cause Analysis

### Cause 1: Why Chicago Unbound & Indiana Law Fail
1. **The `LooksLikePdf` Gatekeeper in `OnNavigationCompleted` (Line 333 of `WebBrowserControl.xaml.cs`)**:
   ```csharp
   string type = await core.ExecuteScriptAsync("document.contentType");
   string plain = type.Trim('"');
   string url = Browser.Source?.ToString() ?? string.Empty;
   bool isPdf = plain.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
   bool viewerDom = plain.Length == 0 || plain.Equals("null", StringComparison.OrdinalIgnoreCase);
   if (!isPdf && !(viewerDom && LooksLikePdf(url)))
   {
       DropVisualGuard(restore: true);
       return;
   }
   ```
   - When Chromium renders its internal PDF viewer plugin, `document.contentType` returns `"null"`, so `viewerDom` is `true`.
   - However, for Chicago Unbound and Indiana Law, the URL is `/cgi/viewcontent.cgi?article=13702&context=journal_articles`.
   - `LooksLikePdf(url)` checks only for `.pdf` or `/pdf/` or `getpdf`. It returns **`false`** for `viewcontent.cgi`!
   - Because `LooksLikePdf(url)` is `false`, `!(viewerDom && LooksLikePdf(url))` is `true`, and line 333 **returns immediately without calling `CatchInlinePdfAsync()`**! Chromium's internal viewer stays open on screen.
   - **Crucial insight**: When `viewerDom` is `true` (`plain == "null"`), Chromium is hosting its internal PDF plugin. Normal web pages ALWAYS return `"text/html"`. Gating `viewerDom` behind `LooksLikePdf` is an architectural error that breaks every academic repository using query strings or CGI scripts.

2. **The `StartEarlyPdfHandoff` Gate in `OnWebResourceResponseReceived` (Line 841)**:
   ```csharp
   if (mainDocument || frameDocument || LooksLikePdf(uri) || UriEquals(uri, _mainNavUri))
       StartEarlyPdfHandoff(uri);
   ```
   - Chicago Unbound and Indiana Law use Cloudflare bot mitigation (`cf-mitigated: challenge`).
   - When Cloudflare finishes verifying and delivers the PDF, `dest` is empty, `LooksLikePdf(uri)` is `false` (`viewcontent.cgi`), and `UriEquals(uri, _mainNavUri)` is `false` if redirect tokens were appended.
   - None of the 4 conditions match, so `StartEarlyPdfHandoff` is never called even though `Content-Type: application/pdf` was confirmed!

### Cause 2: Why Wiley Fails
1. Wiley links (`/doi/pdf/...`) trigger `ArmVisualGuard()` on navigation start because `LooksLikePdf` is `true`. This sets `Browser.Visibility = Visibility.Hidden;`.
2. Wiley immediately serves a Cloudflare challenge page with HTTP 403 Forbidden.
3. In `OnWebResourceResponseReceived`, line 809:
   ```csharp
   if (_browserGuarded && e.Response.StatusCode is >= 200 and < 300 ...)
       DropVisualGuard(restore: true);
   ```
   Because the status code is 403, line 809 **never drops the visual guard**! The browser stays `Visibility.Hidden`, preventing Cloudflare's interactive challenge (Turnstile) from being visible or completed.

### Cause 3: Why the PDF Remains in the Browser and Bugs Out
1. In `StartEarlyPdfHandoff`:
   ```csharp
   handed = await HandPdfToReaderAsync(url, bytes);   // the pane steps aside; the reader tab opens
   _ = Task.Delay(600).ContinueWith(_ => Dispatcher.BeginInvoke(
       (Action)(() => RetreatFromInlinePdf())));
   ```
2. When `HandPdfToReaderAsync` succeeds, it fires `PdfRequested`.
3. In `MainWindow.xaml.cs`:
   ```csharp
   WebPane.PdfRequested += path =>
   {
       HideWebPane();
       OpenInNewTab(path);
       RefreshWebSaveButton(path);
   };
   ```
4. `HideWebPane()` immediately sets `WebPaneHost.Visibility = Collapsed` and calls `WebPane.OnPaneHidden()`, which runs `await core.TrySuspendAsync()`.
5. **The WebView2 engine is suspended immediately!**
   - The delayed `Task.Delay(600)` retreat never executes cleanly on a suspended engine.
   - More critically: **no one removes or closes the PDF tab from `WebPane.Tabs`**!
   - The card in `WebPane.Tabs` stays in the top tab strip and sidebar gallery with the PDF title and URL.
6. When the user re-opens the web browser, the active tab is still the PDF tab pointing to the suspended/unretreated PDF viewer, resulting in a frozen, broken, or bugged state.

---

## 3. Required Architectural Solutions

### A. Remove the `LooksLikePdf` Restriction on Viewer DOMs in `OnNavigationCompleted`
In `OnNavigationCompleted` (`WebBrowserControl.xaml.cs`):
```csharp
bool isPdf = plain.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
bool viewerDom = plain.Length == 0 || plain.Equals("null", StringComparison.OrdinalIgnoreCase);

// Any page that reports application/pdf OR has a viewer DOM ("null") is a PDF candidate!
// Also support common academic repository query patterns (viewcontent.cgi, download=true, etc.)
if (!isPdf && !viewerDom && !LooksLikePdf(url))
{
    DropVisualGuard(restore: true);
    return;
}
```
If `viewerDom` is true or `_pdfCapture` holds bytes, **always** trigger `CatchInlinePdfAsync()`.

### B. Trigger `StartEarlyPdfHandoff` on Any Verified PDF Response
In `OnWebResourceResponseReceived`:
If `isPdf` is true (`application/pdf` or `application/x-pdf`):
- Do NOT gate `StartEarlyPdfHandoff` behind `LooksLikePdf(uri)` or `mainDocument`.
- An incoming PDF payload to the browser is always a document the user intended to open in Avalanche.
- Ensure `StartEarlyPdfHandoff(uri)` is started whenever `isPdf` is true and handoff is not already in flight.

### C. Handle Challenges and Interstitials (Wiley / Cloudflare 403)
- In `OnWebResourceResponseReceived`, do NOT keep the visual guard active if the response is an HTML page (regardless of whether status code is 200, 403, or 429).
- If `!isPdf && _browserGuarded`:
  ```csharp
  DropVisualGuard(restore: true);
  ```
  If a challenge page or Turnstile appears, the user must be able to see and interact with it.

### D. Clean Up Web Browser State Immediately on Successful PDF Handoff
When a PDF is successfully handed to Avalanche reader (`handed == true`):
1. **Immediately clean up the browser before hiding / suspending**:
   - If `WebPane.Tabs.Count > 1` (the PDF opened as a new tab or there were other tabs open):
     - Automatically close the PDF tab: call `CloseTab(url)`.
     - The browser switches back to the previous active web page (e.g. search results or journal index).
   - If `WebPane.Tabs.Count <= 1`:
     - Immediately navigate back (`if (core.CanGoBack) core.GoBack(); else NavigateTo(HomePage);`).
     - Reset the tab card in `Tabs` to the target page URL and title so it doesn't linger as a PDF.
2. **Synchronous Retreat**:
   - Do NOT rely on `Task.Delay(600)` across engine suspension.
   - Perform the retreat or tab closure **before** calling `PdfRequested?.Invoke(target)` (or immediately within `HandPdfToReaderAsync`), and ensure `DropVisualGuard(restore: true)` is executed so the browser is clean for the next time it opens.

---

## 4. Verification Checklist

1. **Test Academic Repositories**:
   - `https://chicagounbound.uchicago.edu/cgi/viewcontent.cgi?article=13702&context=journal_articles`
   - `https://www.repository.law.indiana.edu/cgi/viewcontent.cgi?article=11519&context=ilj`
   - `https://onlinelibrary.wiley.com/doi/pdf/10.1111/j.1468-2230.1957.tb00440.x`
   - Verify all 3 hand over to Avalanche's PDF reader editor.
2. **Verify Browser Tab Cleanup**:
   - Open a search page in the browser (e.g. Google Scholar).
   - Click a PDF link.
   - Once the PDF opens in Avalanche, click the web browser globe button to reopen the browser.
   - Verify the browser is cleanly showing the previous web page (or home page), NOT a bugged/frozen PDF viewer or a lingering PDF tab.
3. **Verify Status Strip**:
   - Verify the status bar cleanly hides without leaving "Fetching the PDF..." stuck.
