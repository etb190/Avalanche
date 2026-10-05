# TASK: Fix Remaining Academic PDF Failures — The Real Root Cause (v1.19.9)

Repository: `https://github.com/etb190/Avalanche`  
Target Files: `Controls/WebBrowserControl.xaml.cs`  
Version: `1.19.9` (or next patch)

---

## 1. Status After v1.19.8

The zombie-tab / browser-bugs-out issue is **fixed** ✅ (`SettleAfterHandoff` works correctly).

The three academic PDF links **still fail to auto-port** to Avalanche's PDF editor:
- `https://chicagounbound.uchicago.edu/cgi/viewcontent.cgi?article=13702&context=journal_articles`
- `https://www.repository.law.indiana.edu/cgi/viewcontent.cgi?article=11519&context=ilj`
- `https://onlinelibrary.wiley.com/doi/pdf/10.1111/j.1468-2230.1957.tb00440.x`

**Critical user observation**: When these PDFs open in Chromium's built-in viewer and the user clicks Chrome's built-in **Save/Download button**, the `OnDownloadStarting` event fires immediately and Avalanche's reader opens the document. The PDF bytes are 100% accessible — only the automatic launch fails.

---

## 2. The REAL Root Cause — Why All Three Capture Layers Fail

### Why `--disable-features=PdfInlineViewer` Does Nothing (Line 177)
The browser argument `--disable-features=PdfInlineViewer` in `EnsureReadyAsync()` is **NOT a supported WebView2 flag**. WebView2 silently ignores unknown Chromium feature flags passed via `AdditionalBrowserArguments`. The built-in PDF viewer remains fully active. PDFs are rendered inline by Chromium's viewer instead of triggering `OnDownloadStarting`. This means the entire PDF interception strategy depends on the three fallback capture layers — and all three fail for these sites.

### Why Layer 1 Fails: `WaitPdfCaptureAsync` / `GetContentAsync()` Returns Null
In `OnWebResourceResponseReceived` (line 926):
```csharp
Stream? content = await e.Response.GetContentAsync();
if (content is null) { capture.TrySetResult(null); return; }
```
**`GetContentAsync()` is a known limitation of WebView2 for PDF responses.** When Chromium renders a PDF in its internal viewer, the response stream is consumed internally by the viewer's rendering pipeline. `GetContentAsync()` returns `null` or throws `HRESULT: 0x800700E8` ("The pipe is being closed"). The capture slot is set to `null`, and `WaitPdfCaptureAsync` returns `null`.

### Why Layer 2 Fails: `FetchViaPageScriptAsync()` Returns Empty
When Chromium renders a PDF, the page DOM is Chromium's internal PDF viewer extension (`chrome-extension://...` or `<embed type="application/pdf">`). Running `fetch(location.href)` inside this context is blocked by Content Security Policy (CSP). The script returns an empty string.

### Why Layer 3 Fails: `FetchBrowserBytesAsync()` Gets 403'd
The out-of-process `HttpClient` fetch carries the session cookies but **NOT the Cloudflare challenge tokens** (`cf_clearance`, `__cf_bm`). Even with cookies copied, Cloudflare's Turnstile validation is tied to the browser's JavaScript execution environment. An `HttpClient` request from a different process looks like a fresh bot and gets 403 Forbidden.

### Summary: The Capture Pipeline Is Fundamentally Broken for These Sites
All three layers return null → `CapturePdfFromBrowserAsync` returns null → `HandPdfToReaderAsync` returns false → handoff fails → the user sees Chromium's viewer.

---

## 3. The Solution: Force WebView2 to Treat PDFs as Downloads

Since `--disable-features=PdfInlineViewer` does not work, we need to use the **only reliable WebView2 API method**: intercept the navigation BEFORE Chromium's PDF viewer can render, and force a download.

### A. Use `NavigationStarting` + `WebResourceRequested` to Force Download Behavior
The correct approach is to use `AddWebResourceRequestedFilter` and `WebResourceRequested` to intercept PDF navigations and modify the response headers to force download behavior:

```csharp
// In EnsureReadyAsync(), after subscribing to events:
core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceRequestedEventArgs...);
```

**However**, the simplest and most reliable approach is:

### B. Cancel Navigation and Re-request as Download (Recommended)
In `OnNavigationStarting`, when a PDF-shaped URL is detected OR when we have evidence a PDF is incoming:

1. **Do NOT cancel navigations** (we already established this breaks one-time tokens).
2. Instead, let the navigation proceed but **monitor the response**.

### C. The Real Fix: Intercept in `OnWebResourceResponseReceived` and Force a Download
When `OnWebResourceResponseReceived` detects `Content-Type: application/pdf`:
1. Instead of trying to read the response stream (which fails), **initiate a programmatic download** using `CoreWebView2.DownloadStarting` by navigating the browser to the same URL with a download hint.
2. OR: Use `CoreWebView2.CallDevToolsProtocolMethodAsync("Page.setDownloadBehavior", ...)` to force download behavior.
3. OR: The simplest approach — **call `PrintToPdfAsync`** on the rendered PDF page.

### D. PrintToPdfAsync — The Reliable Fallback (Simplest Fix)
`PrintToPdfAsync` works on ANY page Chromium has rendered, including PDFs in the built-in viewer. It outputs a valid PDF file. This is officially supported by Microsoft.

**Change `CapturePdfFromBrowserAsync` to add `PrintToPdfAsync` as a 4th fallback layer**:
```csharp
private async Task<byte[]?> CapturePdfFromBrowserAsync(string url)
{
    // Layer 1: Engine's own response stream (often null for PDFs)
    byte[]? captured = await WaitPdfCaptureAsync(TimeSpan.FromSeconds(20));
    if (captured != null && HasPdfHeader(captured)) return captured;
    
    // Layer 2: In-page JavaScript fetch (fails on viewer DOMs)
    byte[]? scripted = await FetchViaPageScriptAsync();
    if (scripted != null && HasPdfHeader(scripted)) return scripted;
    
    // Layer 3: Out-of-process credentialed fetch (fails on Cloudflare)
    byte[]? fetched = await FetchBrowserBytesAsync(url);
    if (fetched != null && HasPdfHeader(fetched)) return fetched;
    
    // Layer 4 (NEW): PrintToPdfAsync — always works on rendered PDFs
    byte[]? printed = await PrintRenderedPdfAsync();
    if (printed != null && HasPdfHeader(printed)) return printed;
    
    return null;
}

private async Task<byte[]?> PrintRenderedPdfAsync()
{
    try
    {
        CoreWebView2? core = Browser.CoreWebView2;
        if (core is null) return null;
        string temp = TempPdfPath("print-capture-" + 
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".pdf");
        await core.PrintToPdfAsync(temp, null);
        byte[] bytes = await File.ReadAllBytesAsync(temp);
        try { File.Delete(temp); } catch { }
        return bytes;
    }
    catch { return null; }
}
```

### E. Even Better: Trigger Download via DevTools Protocol
Use `CallDevToolsProtocolMethodAsync` to programmatically trigger a download of the current page URL. This causes Chromium to re-fetch the URL as a download, which fires `OnDownloadStarting`:
```csharp
// Force current PDF URL to download instead of render
await core.CallDevToolsProtocolMethodAsync("Page.navigate", 
    $"{{\"url\":\"{url}\",\"transitionType\":\"typed\"}}");
```

Or use:
```csharp
// Set download behavior for PDFs
await core.CallDevToolsProtocolMethodAsync("Browser.setDownloadBehavior",
    "{\"behavior\":\"allowAndName\",\"downloadPath\":\"" + tempDir + "\"}");
```

### F. Alternative: `NavigationStarting` Header Injection
Another approach is to add `Content-Disposition: attachment` to the request via `WebResourceRequested`, which forces Chromium to treat the response as a download:
```csharp
core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
core.WebResourceRequested += (s, e) =>
{
    // If we detect this is a PDF navigation, add header to force download
};
```

---

## 4. Recommended Implementation Order

1. **Add `PrintToPdfAsync` as Layer 4** in `CapturePdfFromBrowserAsync`. This is the safest, simplest change with zero risk of breaking existing functionality. It catches every PDF that Chromium's viewer managed to render.
2. **Remove the dead `--disable-features=PdfInlineViewer`** flag to avoid confusion.
3. **Test all three failing URLs** to verify `PrintToPdfAsync` captures them.
4. **Optional future improvement**: Investigate DevTools Protocol `Page.setDownloadBehavior` to prevent Chromium's viewer from ever rendering PDFs, making all PDFs go through `OnDownloadStarting` directly.

---

## 5. Verification Checklist

1. **Test the three still-failing links**:
   - `https://chicagounbound.uchicago.edu/cgi/viewcontent.cgi?article=13702&context=journal_articles`
   - `https://www.repository.law.indiana.edu/cgi/viewcontent.cgi?article=11519&context=ilj`
   - `https://onlinelibrary.wiley.com/doi/pdf/10.1111/j.1468-2230.1957.tb00440.x`
   - All three must auto-port to Avalanche's PDF reader without user intervention.
2. **Verify previously working links still work**:
   - JSTOR, Academia, SSRN links should continue working as before.
3. **Verify the zombie-tab fix still works** (it does — no changes needed there).
4. **Verify status bar** never gets stuck on "Fetching the PDF..."
