# TASK: Add "Open in Avalanche" Extension Button for PDFs Stuck in Chromium's Viewer

Repository: `https://github.com/etb190/Avalanche`  
Target Files: `Controls/WebBrowserControl.xaml.cs`, new extension folder  
Version: `1.19.10` (or next patch)

---

## 1. Context

After 4 versions (v1.19.6–v1.19.9) of trying to automatically extract PDF bytes from Chromium's built-in viewer, the capture pipeline still fails on certain academic sites (Chicago Unbound, Indiana Law, Wiley). The root cause is that WebView2's `GetContentAsync()` returns null for PDFs consumed by the viewer, in-page `fetch()` is blocked by CSP in the viewer extension DOM, the out-of-process fetch gets 403'd by Cloudflare, and `PrintToPdfAsync` does not reliably produce a clean PDF from the viewer.

**However**: clicking Chrome's built-in Save/Download button in the viewer **always works** — it triggers `OnDownloadStarting`, which instantly opens the PDF in Avalanche's reader. The download path is 100% reliable.

### The Solution
Instead of fighting the capture pipeline, add a **small WebView2 browser extension** that injects an "Open in Avalanche" button on any PDF rendered in Chromium's viewer. Clicking it triggers the same download mechanism that Chrome's own Save button uses. This is a user-facing fallback for PDFs that the automatic handoff missed.

**Why this is the right approach:**
- PDFs that auto-port never stay in the viewer long enough for the button to matter.
- PDFs stuck in the viewer get a one-click escape to Avalanche's editor.
- Uses the proven `OnDownloadStarting` path (100% reliable).
- Zero risk to existing auto-handoff logic.
- Extension is bundled with the app, loaded locally, invisible to the user otherwise.

---

## 2. Architecture

### A. The Extension (bundled in `Resources/WebExtensions/avalanche-pdf/`)

**`manifest.json`:**
```json
{
  "manifest_version": 3,
  "name": "Avalanche PDF Helper",
  "version": "1.0",
  "description": "Opens PDFs in Avalanche's reader",
  "content_scripts": [
    {
      "matches": ["<all_urls>"],
      "js": ["content.js"],
      "css": ["button.css"],
      "run_at": "document_idle",
      "match_about_blank": false
    }
  ],
  "permissions": []
}
```

**`content.js`:**
```javascript
// Only activate on Chromium's PDF viewer pages
// The viewer sets document.contentType to "application/pdf"
// or the URL ends with .pdf, or the page is the internal viewer extension
(function() {
  // Check if this is a PDF page
  const isPdf = document.contentType === 'application/pdf'
    || document.querySelector('embed[type="application/pdf"]') !== null;
  
  if (!isPdf) return;

  // Don't inject if button already exists
  if (document.getElementById('avalanche-open-btn')) return;

  // Create the floating button
  const btn = document.createElement('button');
  btn.id = 'avalanche-open-btn';
  btn.title = 'Open in Avalanche';
  btn.setAttribute('aria-label', 'Open in Avalanche PDF Editor');
  
  // The Avalanche logo/icon or a simple document icon
  btn.innerHTML = `
    <svg viewBox="0 0 24 24" width="28" height="28" fill="white">
      <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6z"/>
      <polyline points="14,2 14,8 20,8" fill="none" stroke="white" stroke-width="1.5"/>
      <line x1="9" y1="13" x2="15" y2="13" stroke="currentColor" stroke-width="1.5"/>
      <line x1="9" y1="17" x2="13" y2="17" stroke="currentColor" stroke-width="1.5"/>
    </svg>
  `;

  btn.addEventListener('click', function(e) {
    e.preventDefault();
    e.stopPropagation();
    // Send the current PDF URL to the C# host via WebView2's postMessage
    window.chrome.webview.postMessage(JSON.stringify({
      type: 'avalanche-open-pdf',
      url: window.location.href
    }));
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
  box-shadow: 0 6px 24px rgba(0,0,0,0.4), 0 3px 6px rgba(0,0,0,0.25);
  opacity: 1;
}
#avalanche-open-btn:active {
  transform: scale(0.95);
}
#avalanche-open-btn svg {
  pointer-events: none;
  filter: drop-shadow(0 1px 2px rgba(0,0,0,0.3));
  color: rgba(74,144,217,1);
}
```

### B. Loading the Extension in `WebBrowserControl.xaml.cs`

In `EnsureReadyAsync()`, after initializing the WebView2:
```csharp
// Load the Avalanche PDF helper extension
try
{
    string extPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
        "Resources", "WebExtensions", "avalanche-pdf");
    if (Directory.Exists(extPath))
        await core.Profile.AddBrowserExtensionAsync(extPath);
}
catch { /* extension loading is a courtesy, never a requirement */ }
```

**Important**: The `CoreWebView2EnvironmentOptions` must enable extensions:
```csharp
CoreWebView2EnvironmentOptions options = new()
{
    AreBrowserExtensionsEnabled = true,
};
```

### C. Receiving the Message in C#

Subscribe to `WebMessageReceived` in `EnsureReadyAsync()`:
```csharp
core.WebMessageReceived += OnWebMessageReceived;
```

Handler:
```csharp
private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
{
    try
    {
        string json = e.WebMessageAsJson;
        // Parse the message - expect {"type":"avalanche-open-pdf","url":"..."}
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("type").GetString() != "avalanche-open-pdf") return;
        string? url = doc.RootElement.GetProperty("url").GetString();
        if (string.IsNullOrEmpty(url)) return;
        
        // Trigger a download of the PDF URL — this fires OnDownloadStarting
        // which is the proven 100%-reliable path to Avalanche's reader.
        // The simplest way: use the DevTools protocol to trigger a download.
        _ = TriggerPdfDownloadAsync(url);
    }
    catch { /* malformed messages are silently ignored */ }
}

private async Task TriggerPdfDownloadAsync(string url)
{
    try
    {
        CoreWebView2? core = Browser.CoreWebView2;
        if (core is null) return;
        ShowStatus(TryLoc("Str_Web_PdfOpening"));
        
        // Navigate to the URL with a download disposition.
        // This triggers OnDownloadStarting with the PDF bytes.
        string js = $"(function(){{const a=document.createElement('a');" +
                    $"a.href='{url.Replace("'", "\\'")}';a.download='document.pdf';" +
                    $"document.body.appendChild(a);a.click();document.body.removeChild(a);}})()";
        await core.ExecuteScriptAsync(js);
    }
    catch { HideStatus(); }
}
```

**If the anchor-click download approach fails** (CSP may block it in the viewer DOM), use the alternative:
```csharp
// Alternative: navigate the browser itself to the URL.
// Since the document is already loaded, Chromium will serve it from cache
// and OnDownloadStarting will fire.
// Or simply use the WebView2 download API directly if available.
```

**Simplest alternative in the content script** — instead of `postMessage`, just trigger a download directly:
```javascript
btn.addEventListener('click', function(e) {
  e.preventDefault();
  e.stopPropagation();
  // Create a hidden download link and click it
  const a = document.createElement('a');
  a.href = window.location.href;
  a.download = 'document.pdf';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
});
```
This triggers `OnDownloadStarting` directly without needing `postMessage` at all. If CSP blocks the anchor download inside the viewer DOM, fall back to `postMessage` + C# side download.

### D. Extension File Deployment
- Bundle the extension folder in the project as embedded resources or copy-to-output files.
- In `.csproj`, add:
  ```xml
  <ItemGroup>
    <Content Include="Resources\WebExtensions\avalanche-pdf\**\*">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
  ```

---

## 3. Button Behavior & UX

1. The button **only appears on PDF pages** (where `document.contentType === 'application/pdf'`).
2. It sits in the **bottom-right corner** as a floating circular button with an Avalanche-themed color.
3. It is **semi-transparent** (0.85 opacity) so it doesn't obstruct reading, and becomes fully opaque on hover.
4. Clicking it **triggers a download** of the current PDF URL, which fires `OnDownloadStarting` → the proven path that opens the PDF in Avalanche's reader instantly.
5. For PDFs that auto-port successfully, the user never sees this button because they leave the viewer too fast.

---

## 4. Keep Existing Auto-Handoff Logic

Do NOT remove the existing 4-layer capture pipeline. It still catches many PDFs automatically (JSTOR, SSRN, Academia, direct `.pdf` links, etc.). The extension button is a **fallback for the PDFs that slip through**, giving the user a one-click alternative to manually finding Chrome's Save button.

---

## 5. Verification Checklist

1. **Test the three failing academic links** with the extension button:
   - `https://chicagounbound.uchicago.edu/cgi/viewcontent.cgi?article=13702&context=journal_articles`
   - `https://www.repository.law.indiana.edu/cgi/viewcontent.cgi?article=11519&context=ilj`
   - `https://onlinelibrary.wiley.com/doi/pdf/10.1111/j.1468-2230.1957.tb00440.x`
   - Navigate to each, wait for Chromium's viewer to render, click the Avalanche button → PDF opens in reader.
2. **Verify the button does NOT appear on regular web pages** (Google, DuckDuckGo, journal index pages).
3. **Verify previously auto-porting PDFs still auto-port** (JSTOR, SSRN, Academia) without needing the button.
4. **Verify the button style** — matches Avalanche's aesthetic, doesn't obstruct content.
