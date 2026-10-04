# TASK: Fix In-App Browser PDF Interception & Eliminate "Site Blocked Download" Failures

Repository: `https://github.com/etb190/Avalanche`  
Target File: `Controls/WebBrowserControl.xaml.cs`  
Version: `1.19.4` (or next patch)

---

## 1. Problem Diagnosis: Why 6/10 PDFs Fail with "Site Blocked Download" or Open in Chromium's Viewer

The reader currently encounters two major bugs when navigating and downloading PDFs via the in-app browser:
1. **"This PDF couldn't be fetched - the site blocked the download" (`Str_Web_PdfBlocked`)**:
   - Happens on 50%+ of academic, cloud-storage, and publisher links (ScienceDirect, Springer, JSTOR, Wiley, Cloudflare-protected sites, Google Drive, etc.).
2. **Some PDFs still open inside Chromium's built-in PDF viewer**:
   - Happens on indirect links (`/download?id=...`, dynamic redirects, JavaScript/form submissions) where the URL does not end in `.pdf`.

### Root Causes in `WebBrowserControl.xaml.cs`:
1. **Canceling Navigation and Using External C# `HttpClient`**:
   - In `OnNavigationStarting`:
     ```csharp
     if (LooksLikePdf(e.Uri) && !_pdfTried.Contains(e.Uri))
     {
         e.Cancel = true;
         _ = OpenRemotePdfAsync(e.Uri);
     }
     ```
   - When a user clicks a link, canceling the navigation stops the authentic browser request.
   - `FetchBrowserBytesAsync` then makes a separate out-of-process HTTP request using .NET `HttpClient`. Even though it copies cookies and User-Agent, modern CDNs and anti-bot systems (Cloudflare, Akamai, CloudFront) detect:
     - **Missing Client Hints & Sec-* headers**: (`sec-ch-ua`, `sec-fetch-dest`, `sec-fetch-mode`).
     - **TLS Fingerprint Mismatch**: .NET Schannel/TLS fingerprint does not match real Chromium.
     - **Single-Use Signed Tokens**: Dynamic download tokens get invalidated because the original navigation was aborted.
   - The server answers with HTTP 403 Forbidden, 401, or a Cloudflare CAPTCHA challenge page. `EnsureSuccessStatusCode()` throws, causing `Str_Web_PdfBlocked`.
2. **Permanent Blacklisting in `_pdfTried`**:
   - If a URL fails once, `_pdfTried.Add(url)` keeps it forever in memory. Any subsequent click or retry on that URL is blocked and rejected without even trying.
3. **Indirect / Dynamic Redirects Bypass `LooksLikePdf`**:
   - Links without explicit `/pdf/` or `.pdf` (e.g., tokenized routes) navigate through into Chromium's built-in PDF viewer.
   - When `CatchInlinePdfAsync` tries to recover them via `FetchBrowserBytesAsync`, it hits the exact same `HttpClient` 403/block issue, falls back to `RetreatFromInlinePdf()`, or gets trapped in the viewer.

---

## 2. Architectural Solution

Stop fighting Chromium's network engine. Chromium has already passed Cloudflare checks, negotiated TLS, handled CSRF tokens, and holds the active authenticated session.

### Core Principles:
1. **Native Downloads via Chromium (`DownloadStarting`)**:
   - When a link triggers a file download, let Chromium download it natively through `CoreWebView2.DownloadStarting`.
   - Set `e.Handled = true` to suppress Edge's default UI tray.
   - Set `e.ResultFilePath = target` so Chromium streams the authenticated bytes directly to Avalanche's temporary cache.
   - Zero TLS mismatch, zero 403 blocks.
2. **For Direct PDF Navigation (Inline PDF Viewing)**:
   - When a URL navigates directly to a PDF and renders in the browser, extract the bytes from **within the authenticated browser context**:
     - Option A: Fetch inside the browser page via `core.ExecuteScriptAsync` using native `window.fetch(location.href, {credentials: 'include'})` as Base64/Blob, avoiding external `HttpClient`.
     - Option B: Use `CoreWebView2.WebResourceResponseReceived` to intercept the response stream directly as Chromium receives it.
     - Option C: Set Chromium arguments / settings on `CoreWebView2EnvironmentOptions` to trigger downloads for PDF MIME types rather than rendering them.
3. **Clean Up `_pdfTried` Blacklist**:
   - Do NOT permanently blacklist URLs on error. Only track currently in-flight requests to avoid re-entrant loops. If a download fails, allow retrying.
4. **Fallback Direct Print-to-PDF / Handoff**:
   - Ensure the "Open in Avalanche" button (`WebOpenPdfBtn`) remains a reliable fallback that captures the current document/page immediately.

---

## 3. Required Changes in `Controls/WebBrowserControl.xaml.cs`

1. **Remove `e.Cancel = true` for Downloadable PDF Links in `OnNavigationStarting`**:
   - Let the navigation or download proceed to `OnDownloadStarting`.
2. **Robust `OnDownloadStarting` Handling**:
   - Ensure all MIME types of `application/pdf`, `application/x-pdf`, `application/octet-stream` (when filename ends in `.pdf`) are handled:
   - Stream directly into `TempPdfPath(SafePdfName(suggested))`.
   - Verify `%PDF-` header upon completion and invoke `PdfRequested`.
3. **In-Browser Extraction for Inline PDFs (`CatchInlinePdfAsync`)**:
   - When `document.contentType == "application/pdf"`, instead of spawning `HttpClient` which gets blocked by 403s, read the document stream directly from Chromium or extract via in-page JavaScript `fetch(location.href)` converted to base64.
   - Step back (`GoBack()`) to the offering page once the file is handed to Avalanche.
4. **Ensure Smooth Status Reporting**:
   - Show status "Fetching PDF for reader..." and hide cleanly upon handoff or clear error.

---

## 4. Verification Checklist

1. **Academic & Protected Links**:
   - Test downloading PDFs from ScienceDirect, arXiv, PubMed Central, and cloud storage links.
   - Verify links no longer show "site blocked the download" and load successfully into an Avalanche editor tab.
2. **Zero In-Browser PDF Viewers**:
   - Verify documents do not remain stuck inside Chromium's PDF reader.
3. **Test Suite**:
   - Ensure unit test suite (`dotnet test`) continues to pass with all string localizations intact.
