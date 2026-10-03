# TASK: Fix Hyphen Stripping in Titles & Fix Repaired PDF Temp Path Leak in Discord RPC

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### Overview & Core Issues to Resolve

Two critical bugs in Discord Rich Presence must be fixed:

1. **Hyphens Stripped from Book Titles (e.g. Date Ranges & Compound Words):**  
   For a book titled `"After the Ice A Global Human History, 20,000-5000 BC.pdf"`, Discord Rich Presence displays `"20,000 5000"` with the hyphen stripped out.  
   *Root Cause:* In `Features/Discord/DiscordRpcController.cs`, `SanitizeTitle()` unconditionally replaces all hyphens (`-`) with spaces (`' '`). This destroys number/date ranges (`20,000-5000`, `1939-1945`), hyphenated names (`Cro-Magnon`), and compound words (`Ice-Age`, `Pre-Columbian`, `Self-Care`). Furthermore, because the title is distorted, `DiscordCoverService` fails to match the thumbnail filename in `ThumbnailCache` (which is literally `After the Ice A Global Human History, 20,000-5000 BC.jpg`).

2. **Repaired PDFs Expose Temp Filenames and Break Rich Presence:**  
   When Avalanche opens a damaged PDF and repairs it (or when a book undergoes repair/rasterization), Discord Rich Presence displays:  
   `Killerpdf Repaired Ebd583ae46f547a4a142784761ff39e4` on the profile instead of the real book title!  
   *Root Cause:* When a file is repaired, `FileOperations.cs` sets `_currentFile` to the temporary working copy created via `App.MakeTempFile("repaired")` (e.g. `%LOCALAPPDATA%\Avalanche\Temp\killerpdf_repaired_{guid}.pdf`), while `_originalFile` (and `FileNameLabel.Text`) keeps the user's real file path (e.g. `After the Ice A Global Human History, 20,000-5000 BC.pdf`). `MainWindow.xaml.cs` (lines 1537–1538) was passing `_currentFile` directly to `DiscordRpcController.OnDocumentOpened(_currentFile, ...)`. This leaks the internal temp filename to Discord, breaks book session tracking, and completely breaks cover art lookup (since `killerpdf_repaired_...jpg` does not exist in `ThumbnailCache`).

---

### 1. Fix Hyphen Stripping in `SanitizeTitle` (`Features/Discord/DiscordRpcController.cs`)

* **Do NOT blindly convert hyphens to spaces.**
* In `SanitizeTitle(string filePath)`:
  * Strip the `.pdf` extension.
  * Underscores (`_`) should be converted to spaces (`' '`) because underscores are standard filename space substitutes.
  * Isolated dashes surrounded by spaces (e.g. `" - "`) or runs of multiple dashes (`"--"`, `"---"`) can be normalized, BUT:
  * **Hyphens connecting words or numbers MUST be preserved:**
    * Ranges: `20,000-5000`, `1914-1918`, `1-30`.
    * Hyphenated words: `Cro-Magnon`, `Ice-Age`, `Post-War`, `State-of-the-Art`.
  * Title-casing logic (`CaseWord`) must handle hyphenated tokens by capitalizing each segment (e.g. `Ice-Age`, `20,000-5000 BC`) while keeping uppercase acronyms (`BC`, `AD`, `USA`) and contractions/possessives (`Israel's`) intact.
  * In `DiscordCoverService.CleanTitle(string filePath)`:
    Ensure `CleanTitle` preserves the exact filename (minus `.pdf`), including all hyphens, so it matches files like `After the Ice A Global Human History, 20,000-5000 BC.jpg` in `ThumbnailCache`.

---

### 2. Fix Repaired PDF Temp Path Leak (`MainWindow.xaml.cs` & `PdfViewer.Tabs.cs`)

* **Always use the original document path for display and Discord RPC:**
  1. In `MainWindow.xaml.cs`:
     * In `ActiveDocumentChanged(string? filePath)`:
       Determine the true document path:
       ```csharp
       string? realPath = _originalFile ?? filePath ?? _currentFile;
       ```
       Pass `realPath` to `DiscordRpcController.OnDocumentOpened(realPath, _currentPage + 1, _doc.PageCount)`.
     * Check anywhere else `OnDocumentOpened` is called in `MainWindow` or `MainWindowViewerHost`, and ensure `_originalFile ?? _currentFile` is passed instead of bare `_currentFile`.
  2. In `Controls/Viewer/PdfViewer.Tabs.cs`:
     * When notifying `Host?.ActiveDocumentChanged(...)`, pass `target.OriginalFile ?? target.CurrentFile` instead of bare `target.CurrentFile`.
  3. **Defensive Fallback in `DiscordRpcController.cs` & `DiscordCoverService.cs`:**
     * If the incoming `filePath` contains `killerpdf_` or `_repaired_` or resides inside `App.TempDir`:
       * If `_originalFile` or active window title is available, resolve to it.
       * If only a temp name is present, never display `"Killerpdf Repaired {guid}"` to Discord. Fall back cleanly to `"Reading"` or the active tab title.

---

### 3. Verification & Checks
1. Run `dotnet build` with zero warnings and zero errors.
2. Run `dotnet test` and confirm all tests pass.
3. Open `"After the Ice A Global Human History, 20,000-5000 BC.pdf"`:
   * Verify Discord Rich Presence shows the title with the hyphen intact: `"After the Ice A Global Human History, 20,000-5000 BC"`.
   * Verify `DiscordCoverService` successfully finds `After the Ice A Global Human History, 20,000-5000 BC.jpg` in `ThumbnailCache`, uploads/fetches it, and displays the book cover on Discord.
4. Trigger a repair on a PDF (or open a PDF that requires repair):
   * Verify Discord Rich Presence displays the original book title and its cover, NEVER `"Killerpdf Repaired..."` or temp GUIDs.
