# TASK: UI Polish — Close Buttons, Status Indicator, Bookmarks, Extension Fix, Tab Stability

Repository: `https://github.com/etb190/Avalanche`  
Target Files: `Controls/WebBrowserControl.xaml`, `Controls/WebBrowserControl.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `Resources/WebExtensions/avalanche-pdf/content.js`  
Version: `1.19.11` (or next patch)

---

## Issue 1: Add ✕ Close Button to Sidebar Web Tab Cards

### Current State
The **top tab strip** already has a ✕ close button per tab (`WebTabStripClose_Click` at line 2869 of `MainWindow.xaml`). But the **left sidebar gallery cards** (the `WebTabsPanel` at line 1938, with `WebTabsList` ItemsControl at line 1957) do NOT have any close button. Each card is a `Button` that only navigates to the tab on click — there is no way to close a tab from the sidebar.

### Required Fix
Add a small ✕ button to each sidebar card in `MainWindow.xaml`, inside the card's `DataTemplate` (line 1960–1999). Place it in the top-right corner of the card, overlaying the preview thumbnail:
```xaml
<!-- Inside the card's StackPanel (line 1973), add a close button overlaying the top-right -->
<Grid>
    <!-- existing preview Border + Title + Host TextBlocks -->
    <Button Content="&#xE711;" 
            FontFamily="{DynamicResource IconFont}" FontSize="9"
            Width="18" Height="18" Padding="0"
            HorizontalAlignment="Right" VerticalAlignment="Top"
            Margin="0,2,2,0" Cursor="Hand"
            Style="{DynamicResource TabCloseButton}"
            Tag="{Binding Url}" Click="WebSidebarTabClose_Click"/>
</Grid>
```

Add a handler in `MainWindow.xaml.cs`:
```csharp
private void WebSidebarTabClose_Click(object sender, RoutedEventArgs e)
{
    if (sender is Button { Tag: string url } && url.Length > 0)
        WebPane.CloseTab(url);
}
```

The close behavior should be identical to the top strip's `WebTabStripClose_Click` — it calls `WebPane.CloseTab(url)` which removes the card, switches to the next tab, or hides the browser if it was the last tab.

---

## Issue 2: Replace "Convert to PDF" Button with Inline Status Indicator

### Current State
- There is a "Convert to PDF / Open in Avalanche" button (`WebOpenPdfBtn`, line 110 of `WebBrowserControl.xaml`) after the address bar.
- The status strip (`WebStatus`, line 127) is a separate `TextBlock` at Grid.Row 2 that shows "Fetching the PDF..." text. When it appears, it **adds vertical space** and pushes the WebView down.

### Required Changes

**A. Remove `WebOpenPdfBtn`** (line 110-111 in `WebBrowserControl.xaml`). Delete it entirely.

**B. Replace `WebStatus` TextBlock with an inline icon indicator** placed where `WebOpenPdfBtn` used to be (right after the address bar, same row, no extra vertical space):
- **Default state**: Hidden/collapsed (no space taken).
- **Loading state**: An animated spinning circle icon (use a `RotateTransform` animation on a circular arrow glyph like `&#xE72C;` or a custom Path). Appears in the same toolbar row right after the omnibox.
- **Success state**: Green checkmark icon (`&#xE73E;` in green). Shows for 2 seconds, then auto-hides.
- **Failure state**: Red ✕ icon (`&#xE711;` in red). Shows for 3 seconds, then auto-hides.

**C. Update C# code:**
- `ShowStatus()` → show the spinning indicator
- `HideStatus()` → show green checkmark, then auto-hide after 2s
- `ShowTransientStatus()` → show red ✕, then auto-hide after 3s
- Remove all references to `WebOpenPdfBtn` from `SetChromeEnabled()` (line 532) and the click handler `WebOpenPdfBtn_Click` (line 238).

The indicator should be a small icon (same size as nav buttons, ~24x24) sitting right after the address bar `Border`, inside the same `StackPanel` (line 87). It must NOT add or remove vertical space — only its visibility changes.

---

## Issue 3: Extension Button Not Showing on Wiley (`/doi/pdf/...`)

### The Problem
The extension button works on most PDF viewer pages but does NOT appear on:
`https://onlinelibrary.wiley.com/doi/pdf/10.1111/j.1468-2230.1957.tb00440.x`

### Root Cause
Wiley uses **Cloudflare Turnstile** which first shows a challenge page (HTTP 403, `text/html`). After the challenge is solved, Wiley serves the PDF through its **own custom embedded PDF viewer** — not Chromium's default viewer. Wiley wraps the PDF inside an HTML page with an `<iframe>` or `<object>` tag pointing to the actual PDF URL on a CDN.

The current extension detection (line 6-8 of `content.js`) only checks:
```javascript
const isPdf = document.contentType === 'application/pdf'
    || window.location.href.startsWith('chrome-extension://mhjfbmdgcfjbbpaeojofohoefgiehjai/')
    || document.querySelector('embed[type="application/pdf"]') !== null;
```

This misses:
1. **`<iframe>` with a PDF src** — Wiley and many academic publishers use `<iframe src="https://cdn.example.com/article.pdf">` instead of `<embed>`.
2. **`<object>` with a PDF data/type** — some viewers use `<object data="url" type="application/pdf">`.
3. **Custom viewer wrappers** — the page URL contains `/pdf/` or `/doi/pdf/` but `document.contentType` is `text/html` (it's an HTML wrapper around the PDF).

### Required Fix in `content.js`
Expand the detection to cover all these cases:
```javascript
(function() {
  // Detect PDF pages - both native viewer and embedded/wrapped PDFs
  const isPdf = document.contentType === 'application/pdf'
    || window.location.href.startsWith('chrome-extension://mhjfbmdgcfjbbpaeojofohoefgiehjai/')
    || document.querySelector('embed[type="application/pdf"]') !== null
    || document.querySelector('iframe[src$=".pdf"]') !== null
    || document.querySelector('iframe[src*="application/pdf"]') !== null
    || document.querySelector('object[type="application/pdf"]') !== null
    || document.querySelector('object[data$=".pdf"]') !== null;

  // Also check if the URL itself suggests a PDF page (publisher PDF viewer wrappers)
  const urlPath = window.location.pathname.toLowerCase();
  const urlLooksPdf = urlPath.endsWith('.pdf')
    || /\/pdf\//.test(urlPath)
    || /\/doi\/pdf\//.test(urlPath)
    || /getpdf/i.test(urlPath)
    || /viewcontent\.cgi/i.test(urlPath);

  if (!isPdf && !urlLooksPdf) return;
  if (document.getElementById('avalanche-open-btn')) return;

  // ... rest of button creation code unchanged ...
  
  // For wrapper pages (urlLooksPdf but not isPdf), try to find the actual PDF URL
  // from an iframe/embed/object on the page
  btn.addEventListener('click', function(e) {
    e.preventDefault();
    e.stopPropagation();
    
    // Try to find the actual PDF URL from embedded elements
    var pdfUrl = window.location.href;
    var embed = document.querySelector('embed[type="application/pdf"]');
    var iframe = document.querySelector('iframe[src$=".pdf"]') 
              || document.querySelector('iframe[src*="pdf"]');
    var obj = document.querySelector('object[type="application/pdf"]')
           || document.querySelector('object[data$=".pdf"]');
    
    if (embed && embed.src) pdfUrl = embed.src;
    else if (iframe && iframe.src) pdfUrl = iframe.src;
    else if (obj && (obj.data || obj.getAttribute('data'))) pdfUrl = obj.data || obj.getAttribute('data');
    
    try {
      var a = document.createElement('a');
      a.href = pdfUrl;
      a.download = 'document.pdf';
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
    } catch(err) {
      try {
        window.chrome.webview.postMessage(JSON.stringify({
          type: 'avalanche-open-pdf',
          url: pdfUrl
        }));
      } catch(e2) {}
    }
  });
  
  document.body.appendChild(btn);
})();
```

Also consider running the detection on a **`MutationObserver`** or a delayed re-check (e.g. `setTimeout` of 2 seconds), since Wiley's viewer may load the `<iframe>` after `document_idle`.

---

## Issue 4: Replace Quick-Access Dial Chips with Bookmarks System

### Current State
Below the address bar there is a `StackPanel` (line 116–122 of `WebBrowserControl.xaml`) with hardcoded "dial chips" for Archive.org, Google Scholar, Gutenberg, arXiv, and PubMed. These were never requested by the user.

### Required Changes

**A. Remove the hardcoded dial chips** (lines 115–122 of `WebBrowserControl.xaml`). Delete the entire `StackPanel` with the `WebDialChip` buttons.

**B. Add a Bookmark button** in the toolbar row (Grid.Row 0), right after the status indicator (Issue 2), separated by a vertical border/divider:
```xaml
<!-- Divider after status indicator -->
<Border Width="1" Height="18" Background="{DynamicResource CardBorderBrush}" Margin="4,0"/>
<!-- Bookmark button -->
<Button x:Name="WebBookmarkBtn" Content="&#xE728;" Style="{StaticResource WebNavBtn}"
        Click="WebBookmarkBtn_Click" ToolTip="{DynamicResource Str_TT_WebBookmark}"/>
```
- `&#xE728;` is the "Add to favorites" star icon in Segoe MDL2 Assets.
- When the current page is already bookmarked, show a filled star (`&#xE735;`) instead.

**C. Bookmark behavior:**
- **Click on unfilled star**: Opens a small popup/flyout with:
  - A text field pre-filled with the page title (editable — the user can rename it or leave it as the URL).
  - A "Save" button that adds the bookmark.
- **Click on filled star**: Removes the bookmark.
- **Storage**: Save bookmarks as a simple JSON file in the app's data directory (e.g., `WebView2Data/bookmarks.json`). Each entry: `{ "name": "...", "url": "...", "favicon": "..." }`.

**D. Display bookmarks** where the dial chips used to be (Grid.Row 1 area). Use a horizontal wrapping `WrapPanel` or `ItemsControl`:
- Each bookmark shows as a small chip with the site's favicon (or a globe icon if unavailable) and the bookmark name.
- Clicking a bookmark navigates to its URL.
- Right-clicking (or long press) shows a context menu with "Edit" and "Delete" options.
- If there are no bookmarks, show a subtle hint: "Bookmark pages with ☆".

---

## Issue 5: Tabs Should NOT Reorder When Switching Between Them

### Current State
In `UpdateTabCardAsync()` (line 1228) and `OpenNewTab()` (line 1196), when a user **revisits** an existing tab:
```csharp
int at = Tabs.IndexOf(card);   // a revisit moves back to the top - the newest end
if (at > 0) { Tabs.RemoveAt(at); Tabs.Insert(0, card); }
```
This moves the tab card back to position 0 (top) every time the user clicks on it or navigates to it. This causes tabs to **jump around** in the sidebar list every time the user switches between them, which is disorienting.

### Required Fix
Tabs should be sorted by **creation order** (newest on top, oldest on bottom) and should **stay in that position** until closed. Only a **brand new** tab gets inserted at position 0. Revisiting an existing tab should only update the `IsActive` flag — NOT move the card.

**In `UpdateTabCardAsync()` (line 1263–1266):** Remove the reordering:
```csharp
// BEFORE (moves revisited tab to top):
int at = Tabs.IndexOf(card);
if (at > 0) { Tabs.RemoveAt(at); Tabs.Insert(0, card); }

// AFTER (leave it where it is):
// Do nothing — the card stays at its creation position.
// Only update its Title, Host, Thumb, and IsActive flag.
```

**In `OpenNewTab()` (line 1210–1213):** Same fix for the revisit branch:
```csharp
// BEFORE:
int at = Tabs.IndexOf(card);
if (at > 0) { Tabs.RemoveAt(at); Tabs.Insert(0, card); }

// AFTER:
// Tab already exists — just activate it, don't move it.
```

The `Tabs.Insert(0, card)` for **new** cards (lines 1207 and 1260) should remain unchanged — new tabs still go to the top.

---

## Summary

| # | Issue | Change |
|---|-------|--------|
| 1 | No close button on sidebar tab cards | Add ✕ overlay button to each card |
| 2 | "Convert to PDF" button + text status bar | Replace with inline animated icon indicator (spinner → ✓/✕) |
| 3 | Extension missing on Wiley | Detect `<iframe>`, `<object>`, and URL-based PDF wrappers |
| 4 | Unwanted hardcoded dial chips | Replace with user bookmark system (star button + chips) |
| 5 | Tabs jump when switching | Only insert new tabs at top; revisits don't move |
