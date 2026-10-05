# TASK: Two-Pronged Fix — Extension Button Fallback + Fix Automatic PrintToPdf Capture

Repository: `https://github.com/etb190/Avalanche`  
Target Files: `Controls/WebBrowserControl.xaml.cs`, new extension folder  
Version: `1.19.10` (or next patch)

---

## PART A: Fix the Automatic `PrintRenderedPdfAsync` (Layer 4)

### Why Layer 4 Failed in v1.19.9

The `PrintRenderedPdfAsync` method (line 608) has two bugs preventing it from working:

#### Bug 1: `document.contentType` Runs in the Wrong Frame
Chromium's PDF viewer is a **two-frame structure**:
- **Outer frame** (top-level): An HTML shell page (`text/html`) that hosts the viewer UI (toolbar, scroll, zoom).
- **Inner frame**: The actual PDF content rendered by the `chrome-extension://mhjfbmdgcfjbbpaeojofohoefgiehjai/` extension.

When `ExecuteScriptAsync("document.contentType")` runs at line 615, it executes in the **outer (top-level) frame**, which returns `"text/html"` — NOT `"null"` or `"application/pdf"`.

Because `viewerDom` and `pdfDoc` are both `false`, line 619 returns `null` immediately:
```csharp
if (!viewerDom && !pdfDoc) return null;   // ← exits here! Never reaches PrintToPdfAsync!
```

**Fix**: Do NOT gate `PrintRenderedPdfAsync` behind `document.contentType`. By the time Layer 4 runs, Layers 1–3 have already failed. The only question that matters is: "Is the browser currently showing a PDF?" Check the **URL** instead:
```csharp
private async Task<byte[]?> PrintRenderedPdfAsync()
{
    try
    {
        CoreWebView2? core = Browser.CoreWebView2;
        if (core is null) return null;
        string url = Browser.Source?.ToString() ?? string.Empty;
        
        // By this point, layers 1-3 all failed. If we're here and the URL
        // looks like a PDF or we have a pending capture, try the print.
        // Also check if the page is Chromium's viewer by probing for its
        // specific extension URL pattern.
        bool onViewer = url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
            || LooksLikePdf(url)
            || _pdfCapture is not null;  // a capture was attempted = we know a PDF arrived
        if (!onViewer) return null;
        
        DropVisualGuard(restore: true);
        string target = TempPdfPath(
            "print-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".pdf");
        if (!await core.PrintToPdfAsync(target, null)) return null;
        byte[] printed = await File.ReadAllBytesAsync(target);
        try { File.Delete(target); } catch { }
        return printed;
    }
    catch { return null; }
}
```

#### Bug 2: `PrintToPdfAsync` on the Viewer Produces a Re-rendered PDF, Not the Original
`PrintToPdfAsync` prints the **page as Chromium sees it**. When the PDF viewer is active, it re-rasterizes the rendered content through the print pipeline. The output:
- IS a valid PDF (has `%PDF-` header, passes `HasPdfHeader`).
- BUT may look slightly different from the original (re-rendered text, possibly different page sizes/margins).
- This is still a **usable document** — all the text and images are there. The user can read and annotate it in Avalanche.

If `PrintToPdfAsync` returns `false` or throws (which it might on some Chromium versions for extension-hosted pages), the method returns `null` and the extension button (Part B below) serves as the guaranteed fallback.

#### Additional Fix: The `LooksLikePdf` Gap for CGI URLs
`LooksLikePdf` still doesn't match `viewcontent.cgi` URLs. Since `PrintRenderedPdfAsync` is the last automatic layer, it should also check if `_pdfCapture` was ever set (meaning `OnWebResourceResponseReceived` confirmed a `Content-Type: application/pdf` arrived). If a capture was attempted, we know a PDF was delivered regardless of URL shape.

---

## PART B: Add "Open in Avalanche" Extension Button

For PDFs where even the fixed Layer 4 fails, add a visible one-click escape button.

### How It Works
A tiny WebView2 browser extension (3 files, ~2KB total) bundled with the app:
1. **Detects PDF viewer pages** — checks `document.contentType === 'application/pdf'` OR the page is `chrome-extension://mhjfbmdgcfjbbpaeojofohoefgiehjai/` (Chromium's PDF viewer extension ID).
2. **Injects a floating "Open in Avalanche" button** — bottom-right corner, semi-transparent, circular.
3. **On click, triggers a download** — creates an `<a download>` link and clicks it, which fires `OnDownloadStarting` — the same path as Chrome's Save button (proven 100% reliable).

### Extension Files (bundle in `Resources/WebExtensions/avalanche-pdf/`)

**`manifest.json`:**
```json
{
  "manifest_version": 3,
  "name": "Avalanche PDF Helper",
  "version": "1.0",
  "description": "Opens PDFs in Avalanche reader",
  "content_scripts": [
    {
      "matches": ["<all_urls>"],
      "js": ["content.js"],
      "css": ["button.css"],
      "run_at": "document_idle"
    }
  ],
  "permissions": []
}
```

**`content.js`:**
```javascript
(function() {
  // Detect Chromium's PDF viewer:
  // 1. document.contentType is 'application/pdf'
  // 2. We're inside the PDF viewer extension
  // 3. There's an embed[type="application/pdf"] on the page
  const isPdf = document.contentType === 'application/pdf'
    || window.location.href.startsWith('chrome-extension://mhjfbmdgcfjbbpaeojofohoefgiehjai/')
    || document.querySelector('embed[type="application/pdf"]') !== null;

  if (!isPdf) return;
  if (document.getElementById('avalanche-open-btn')) return;

  const btn = document.createElement('button');
  btn.id = 'avalanche-open-btn';
  btn.title = 'Open in Avalanche';
  btn.innerHTML = '<svg viewBox="0 0 24 24" width="28" height="28" fill="white">' +
    '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6z"/>' +
    '<polyline points="14,2 14,8 20,8" fill="none" stroke="white" stroke-width="1.5"/>' +
    '<line x1="9" y1="13" x2="15" y2="13" stroke="rgba(74,144,217,1)" stroke-width="1.5"/>' +
    '<line x1="9" y1="17" x2="13" y2="17" stroke="rgba(74,144,217,1)" stroke-width="1.5"/>' +
    '</svg>';

  btn.addEventListener('click', function(e) {
    e.preventDefault();
    e.stopPropagation();
    // Method 1: Try anchor download (triggers OnDownloadStarting)
    try {
      var a = document.createElement('a');
      a.href = window.location.href;
      a.download = 'document.pdf';
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
    } catch(err) {
      // Method 2: If anchor fails (CSP), use postMessage to C# host
      try {
        window.chrome.webview.postMessage(JSON.stringify({
          type: 'avalanche-open-pdf',
          url: window.location.href
        }));
      } catch(e2) {}
    }
  });

  document.body.appendChild(btn);
})();
```

**`button.css`:**
```css
#avalanche-open-btn {
  position: fixed;
  bottom: 24px;
  right: 24px;
  z-index: 2147483647;
  width: 52px;
  height: 52px;
  border-radius: 50%;
  border: none;
  background: linear-gradient(135deg, #4A90D9 0%, #357ABD 100%);
  box-shadow: 0 4px 16px rgba(0,0,0,0.3), 0 2px 4px rgba(0,0,0,0.2);
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: transform 0.15s ease, box-shadow 0.15s ease, opacity 0.3s ease;
  opacity: 0.85;
  padding: 0;
}
#avalanche-open-btn:hover {
  transform: scale(1.1);
  box-shadow: 0 6px 24px rgba(0,0,0,0.4);
  opacity: 1;
}
#avalanche-open-btn:active { transform: scale(0.95); }
#avalanche-open-btn svg { pointer-events: none; }
```

### C# Wiring in `WebBrowserControl.xaml.cs`

**1. Enable extensions in `EnsureReadyAsync()`:**
```csharp
CoreWebView2EnvironmentOptions options = new()
{
    AreBrowserExtensionsEnabled = true,
};
```

**2. Load the extension after init:**
```csharp
try
{
    string extPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
        "Resources", "WebExtensions", "avalanche-pdf");
    if (Directory.Exists(extPath))
        await core.Profile.AddBrowserExtensionAsync(extPath);
}
catch { /* extension loading is a courtesy */ }
```

**3. Handle postMessage fallback (if anchor download fails):**
```csharp
core.WebMessageReceived += OnWebMessageReceived;

private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
{
    try
    {
        string json = e.WebMessageAsJson;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("type").GetString() != "avalanche-open-pdf") return;
        string? url = doc.RootElement.GetProperty("url").GetString();
        if (string.IsNullOrEmpty(url)) return;
        ShowStatus(TryLoc("Str_Web_PdfOpening"));
        _ = FetchAndHandOffAsync(url);
    }
    catch { }
}

private async Task FetchAndHandOffAsync(string url)
{
    try
    {
        byte[]? bytes = await FetchBrowserBytesAsync(url);
        if (bytes != null && HasPdfHeader(bytes))
        {
            string target = TempPdfPath(SafePdfName("extension-download.pdf"));
            await File.WriteAllBytesAsync(target, bytes);
            SettleAfterHandoff(url);
            HideStatus();
            PdfRequested?.Invoke(target);
            return;
        }
        ShowTransientStatus(TryLoc("Str_Web_PdfBlocked"));
    }
    catch { HideStatus(); }
}
```

**4. Deploy extension files in `.csproj`:**
```xml
<ItemGroup>
  <Content Include="Resources\WebExtensions\avalanche-pdf\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

---

## 3. Summary: Defense in Depth

| Layer | Method | Catches |
|-------|--------|---------|
| 1 | `GetContentAsync()` stream capture | Direct `.pdf` URLs, JSTOR, SSRN, Academia |
| 2 | In-page `fetch()` script | Pages where stream was consumed but JS context works |
| 3 | Out-of-process `HttpClient` with cookies | Sites without bot protection |
| 4 | **`PrintToPdfAsync` (FIXED)** | PDFs stuck in viewer where URL or capture confirms PDF |
| 5 | **Extension "Open in Avalanche" button (NEW)** | Everything else — user clicks once, triggers download path |

The first 4 layers are automatic (no user action). Layer 5 is a visible one-click fallback that uses the proven `OnDownloadStarting` path.

---

## 4. Verification Checklist

1. **Auto-handoff (Layers 1–4)**: Test JSTOR, SSRN, Academia — should still auto-port.
2. **Fixed Layer 4**: Test Chicago Unbound, Indiana Law, Wiley — may now auto-port via `PrintToPdfAsync` with the frame/gate fix.
3. **Extension button (Layer 5)**: If any PDF still lands in Chrome's viewer, verify the button appears bottom-right and clicking it opens the PDF in Avalanche instantly.
4. **Button does NOT appear** on regular web pages (Google, search results, journal indexes).
5. **Status bar** never gets stuck.
6. **Zombie tabs** remain fixed (v1.19.8 `SettleAfterHandoff` still in place).
