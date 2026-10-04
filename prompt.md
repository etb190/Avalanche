# TASK: Avalanche Improvements & Axo Notes Integration

Repository: `https://github.com/etb190/Avalanche`  
Status: EXECUTED (v1.18.8: sections 1, 2, and 3's AxoNotesService + notes-card
chip; v1.18.9: per-note check/X save feedback on the chip, superseding the
flash; v1.18.10: the section-3 Summary Navigator button and the popup Save
action were removed again at the owner's direct order - "the save to axo
button is in notes not in summary it should never be in the summarizer" -
and stay out. Section 4: the CI workflow runs both test projects as gates on
every build; the suite is green at every delivered version.)

---

## 1. Summary Navigator: Fix "Explain" Source Text Handoff

### The Problem
When the reader clicks **Explain** on a word or passage in the Summary Navigator (`SummaryWindow`), the AI model frequently reports:
> *"Source Context: The provided raw source text contains only the placeholder '[p. 1]' and no actual content from the book. Consequently, there is no passage in the supplied material that mentions..."*

#### Root Causes:
1. **Unrestored Page Range on Digest Reload**:
   - `_runFirstPage` and `_runLastPage` default to `1` in `SummaryWindow.xaml.cs`.
   - When a previously generated summary is restored from settings via `RestoreDigest()` (on document load or when switching tabs back to the document), only `summary.digest.<documentId>` is loaded into `_fullText` and `DocBox`.
   - The page boundaries (`_runFirstPage` and `_runLastPage`) that originally produced that summary are never restored; they remain `1` and `1`.
   - `_cachedRangeRawText` is `null` upon restoration.
2. **Fallback Extracts Only Page 1**:
   - When `ExplainFromRangeAsync` runs:
     ```csharp
     if (!string.IsNullOrEmpty(_cachedRangeRawText)
         && _cachedRangeFirst == _runFirstPage && _cachedRangeLast == _runLastPage)
     {
         raw = _cachedRangeRawText;
     }
     else
     {
         raw = await PageSummarizer.ExtractRangeAsync(_filePath, _runFirstPage, _runLastPage, ct);
         ...
     }
     ```
   - Because `_runFirstPage == 1` and `_runLastPage == 1`, it extracts only Page 1 of the PDF.
   - In most books, Page 1 is an un-OCR'd cover graphic, title leaf, or blank flyleaf.
   - `ExtractRangeAsync` on Page 1 yields only `"[p. 1]"` with no text.
   - `PageSummarizer.ExplainExcerptAsync` sends literal empty text (`[p. 1]`) to the model, forcing the model to report that the source text is missing and guess from general knowledge.

### Required Architecture & Fix:
1. **Persist the Generation Page Range**:
   - Whenever a digest completes successfully (`FinishSuccess` / `done`), save the exact range alongside the digest text:
     - `summary.digest.<documentId>.first` = `_runFirstPage`
     - `summary.digest.<documentId>.last` = `_runLastPage`
2. **Restore Page Range in `RestoreDigest()`**:
   - When `RestoreDigest()` loads `summary.digest.<documentId>`, also read:
     - `_runFirstPage` from `summary.digest.<documentId>.first` (falling back to `_startPage` if absent).
     - `_runLastPage` from `summary.digest.<documentId>.last` (falling back to `_startPage + _rangePages - 1` if absent).
3. **Defensive Range Fallback in `ExplainFromRangeAsync`**:
   - If `_runFirstPage <= 0 || _runLastPage < _runFirstPage || (_runFirstPage == 1 && _runLastPage == 1 && _pageCount > 1 && _rangePages > 1)`:
     - Fall back to the active range currently showing in the window: `first = _startPage`, `last = RangeEnd()`.
   - Ensure the raw extraction covers the actual multi-page block rather than a single empty cover leaf.

---

## 2. PDF Editor: Asynchronous In-Document Search (Prevent Freezes & UI Lockups)

### The Problem
Using the regular PDF search in the document viewer freezes or locks up Avalanche ("Not Responding") on medium to large documents (100–1,000+ pages).

#### Root Causes:
1. **Synchronous Full-Document Scan on the WPF UI Thread**:
   - In `Shell/Search.cs`, the search debounce timer is a WPF `DispatcherTimer` whose `Tick` event fires directly on the UI thread.
   - When it ticks, it calls `Search.Run(q)`, which calls `SearchService.Search(_host.CurrentFile, query)`.
   - `SearchService.Search` synchronously opens the PDF file and iterates through every single page (`for int pi = 0; pi < doc.NumberOfPages; pi++`), calling `doc.GetPage(pi + 1).GetWords()` with PdfPig.
   - In a 300 to 1,000-page book, parsing thousands of glyphs and calculating bounding boxes across hundreds of pages takes 5 to 25+ seconds.
   - Because this execution happens synchronously on the UI thread, the entire WPF message pump is frozen.
2. **No Cancellation on New Input**:
   - If the user types additional characters, presses Backspace, or closes the search bar, the UI thread is already stuck in the synchronous loop and cannot cancel the old search.
3. **Quadratic String Allocations for Multi-Word Phrases**:
   - In `FindMatchesOnPage`, multi-word phrase matching runs an $O(N^2)$ nested loop per page with repeated string concatenations (`combined += " " + words[j].Text`), causing massive GC pressure and CPU stalls on dense pages.

### Required Architecture & Fix:
1. **Offload Search to Background Thread (`Task.Run`)**:
   - Make `SearchController.RunAsync` asynchronous with a `CancellationToken`.
   - Run the PdfPig search loop in `Task.Run` off the UI thread so the WPF UI stays 100% responsive (60fps scrolling, typing, window moving).
2. **Support Cancellation**:
   - Maintain a `CancellationTokenSource? _searchCts`.
   - Whenever the search query changes or the search bar is closed, cancel the previous in-flight search immediately (`_searchCts?.Cancel()`).
   - `SearchService.SearchAsync` should accept `CancellationToken ct` and check `ct.ThrowIfCancellationRequested()` after each page.
3. **Progressive / Batched Result Updates**:
   - Dispatch results back to the UI thread in batches (or on completion) so the user sees results appear without waiting for the entire 1,000 pages to finish before seeing the first hit.
4. **Optimize Phrase Matching**:
   - Replace quadratic string concatenation with a sliding window `StringBuilder` or word-index matching to eliminate GC memory churn.

---

## 3. Direct Integration with Axo Notes (Books & Articles)

### Goal
Allow the user to send generated summaries, AI digests, or notes directly from Avalanche into Axo's notes database for the current Book or Article with a single click.

### Axo Architecture & Schema Specifications
Axo (`c:\Users\PC\Desktop\Coding\Axo`) is an Electron + React application with TipTap-based rich text note editors. It stores notes as JSON files on the local drive:
- **Books Notes Directory:** `C:\Users\PC\Desktop\database\Books\Notes\`
- **Articles Notes Directory:** `C:\Users\PC\Desktop\database\Articles\Notes\`
- **File Naming:** `<FileNameWithoutExtension>.json` (e.g. `Homo Deus.json` for `Homo Deus.pdf`).

#### Axo Note Object Schema
Each JSON file contains an array of objects:
```json
[
  {
    "id": "1784916178948",
    "content": "<p><strong>Pages 1-20 Summary:</strong></p><p>Summary text here...</p>",
    "startPage": "1",
    "endPage": "20",
    "type": "general",
    "timestamp": "2026-10-03T23:55:00.000Z"
  }
]
```

### Strict Folder Constraint (IMPORTANT)
**Do NOT create or append notes if the document is NOT located in the appropriate folder in the database.**
- Path check rule:
  - If the active PDF is in `C:\Users\PC\Desktop\database\Books\` (or any subfolder):
    - Target: `C:\Users\PC\Desktop\database\Books\Notes\<Title>.json`
  - If the active PDF is in `C:\Users\PC\Desktop\database\Articles\` (or any subfolder):
    - Target: `C:\Users\PC\Desktop\database\Articles\Notes\<Title>.json`
  - **If the PDF is located anywhere else (e.g. Downloads, Desktop root, external drive, Temp):**
    - **Do NOT create or append any notes.**
    - If the button is clicked, disable the action or display a gentle toast notification: *"Document is not in the database Books or Articles folder. Axo note not saved."*

### Implementation Architecture in Avalanche:
1. **Helper Service: `AxoNotesService.cs`**:
   - `bool CanSaveToAxo(string? pdfFilePath, out string? notesJsonPath)`:
     - Normalizes paths (case-insensitive Windows paths).
     - Checks if `pdfFilePath` starts with `C:\Users\PC\Desktop\database\Books\` or `C:\Users\PC\Desktop\database\Articles\`.
     - Returns false if outside those folders.
     - Constructs target path `...\Notes\<Path.GetFileNameWithoutExtension(pdfFilePath)>.json`.
   - `Task<bool> AppendNoteAsync(string pdfFilePath, string markdownContent, int startPage, int endPage)`:
     - Checks `CanSaveToAxo`. If false, returns `false`.
     - Ensures the `Notes` directory exists.
     - Converts markdown formatting (paragraphs, bold, italic, bullet lists) into clean HTML (`<p>`, `<strong>`, `<em>`, `<ul><li>`).
     - Reads existing JSON array (or initializes `new List<AxoNoteItem>()` if file does not exist).
     - Appends new item with:
       - `id`: `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()`
       - `content`: formatted HTML string
       - `startPage`: `startPage.ToString()`
       - `endPage`: `endPage.ToString()`
       - `type`: `"general"`
       - `timestamp`: `DateTime.UtcNow.ToString("o")`
     - Serializes with `JsonSerializerOptions { WriteIndented = true }` and writes atomically to file.
2. **UI Integration Points**:
   - **Sidebar Digest Cards (`SidebarNotes.cs`)**:
     - Add a "Save Note to Axo" icon button on each note card **immediately to the left of the Copy note button**.
     - Style identical to the Copy button: same dimensions (22x20), same styling (`DarkButton`), flat chip look, matching green or accent brush, but with a note / document icon (e.g. `\uE70B` or `\uE82D` in Segoe MDL2 Assets).
     - ToolTip: "Save to Axo Notes" (or localized string).
     - Visual feedback: flash to check mark (`\u2713`) upon successful save, matching `FlashCopyButton`.
   - **Summary Navigator Window (`SummaryWindow.xaml`)**:
     - Add a "Send to Axo Notes" / "Save Note" button in the action bar.
     - Automatically passes `_runFirstPage`, `_runLastPage`, and the generated summary markdown.
     - Shows visual feedback on success (e.g. button state changes to "Saved to Axo ✓" temporarily).
   - **Selection Action Popup (Explain / AI responses)**:
     - Include a "Save to Notes" action on generated explanations.

---

## 4. Verification & Acceptance Checklist
1. **Explain Verification**:
   - Open a document with a previously restored summary.
   - Highlight any passage or word and click **Explain**.
   - Verify the AI prompt receives the full unabridged text of the actual pages (e.g. pages 1–20 or 40–60) rather than just `[p. 1]`.
   - Verify the AI successfully cites the relevant page numbers (`[p. N]`) and explains the concept using the author's actual text.
2. **Search Verification**:
   - Open a 500+ page PDF book.
   - Press Ctrl+F and type a search term.
   - Verify the UI does NOT freeze while typing or searching.
   - Typing quickly or pressing Backspace cleanly cancels previous search tasks without crashing.
   - Matches and highlights appear accurately on the pages.
3. **Axo Notes Integration Verification**:
   - Open a book located in `C:\Users\PC\Desktop\database\Books\...`.
   - Generate a summary or note and click "Send to Axo Notes".
   - Verify `<BookName>.json` in `C:\Users\PC\Desktop\database\Books\Notes\` receives the new note object with matching pages and clean HTML.
   - Open Axo, open that book's notes, and verify the note displays properly in Axo's TipTap editor.
   - Repeat test for an article located in `C:\Users\PC\Desktop\database\Articles\...`.
   - Open a PDF from another folder (e.g. `Downloads` or `Desktop`).
   - Click "Send to Axo Notes" and verify that no note file is created and the user is informed that the file is not in the database folder.
4. **Test Suite**:
   - All 2,011 unit tests continue to pass (`dotnet test`).
