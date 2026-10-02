# TASK: Implement "Recap" Companion in Avalanche (SummaryWindow Styling + Top Bar Toggle)

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### Overview & Core Architecture
Implement the **Recap** feature in Avalanche as a clean companion window that gives the reader an immediate 3–4 sentence memory bridge of the page they just left when turning pages.

Unlike the browser extension's floating popup, the Recap window in Avalanche must **look exactly like `SummaryWindow`** (built with `DialogChrome.Frame`, rounded card, theme styling, pop-in entrance animation, Escape-to-close), but **minus the controls — just the clean content**.

The toggle switch to enable/disable Recap is placed **in the top middle bar of `SummaryWindow`**, across from "Avalanche" on the left and across from the AI Tester beaker icon on the right, using the exact same iOS-style sliding toggle designed for `AiTestWindow`.

---

### 1. The Recap Toggle in `SummaryWindow` Title Bar
* In `Features/Summary/SummaryWindow.xaml.cs` (in `DialogChrome.Frame` title bar extras):
  * Position a toggle switch in the **top middle bar**, centered between the "Avalanche" title wordmark on the far left and the AI Tester beaker button on the right.
  * Use the **exact same iOS-style sliding toggle** (`TestModeToggle` from `AiTestWindow.xaml`):
    * 40px track, 16px white thumb gliding 18px in 150ms on check/uncheck.
    * Paired with a bold label `Recap` (`Str_Lbl_RecapMode`).
  * State: Persisted in user settings (`recap.enabled`).
  * Checked = Recap mode active; Unchecked = Recap mode inactive (dismisses any open Recap window).

---

### 2. The Recap Window (`Features/Summary/RecapWindow.xaml`, `RecapWindow.xaml.cs`)
Create an owned companion window styled identically to `SummaryWindow`:
1. **Window Chrome:**
   * Composed with `DialogChrome.Frame(this, owner, title, Close, BodyRoot, ...)`:
     * Title: `Recap — Page {oldPage}` (or `Recap — Pages {start}–{end}`).
     * Title bar includes the standard close button (`✕`) styled via `DressCloseChip`.
     * Hit-testable corner resize grips via `WindowChrome` (two-axis resizing).
     * Pop-in entrance animation (`WindowFx.PlayOpenPop`), fade close, and Escape-to-close.
2. **Body (Content-Only, Zero Clutter):**
   * **No controls:** No range chips, no word count dropdowns, no stepper arrows, no reset buttons.
   * **Content Area:** Clean, comfortable reading surface displaying the 3–4 sentence recap:
     * Dark theme background (`PaneBrush`), smooth line height (1.6x), clean typography.
     * Shows a subtle indicator while condensing: `"Condensing page…"` (`Str_Recap_Loading`).
     * Internal smooth scrollbar if text expands.
3. **Placement & Memory:**
   * Remembers position and dimensions across sessions via user settings (`recap.win.left`, `recap.win.top`, etc.).

---

### 3. Navigation Trigger Mechanism
In the document viewer navigation handler (e.g. `MainWindowViewerHost.cs` or `MainWindow.xaml.cs` page change event):
* When `recapMode` is enabled and the user navigates to a new page (`oldPage != newPage`):
  * Trigger `ShowRecap(oldPage)` for the page the reader just left.
  * If `RecapWindow` is already open, smoothly update its content to the new page recap rather than stacking windows.

---

### 4. AI Condensation Pipeline & Caching
1. **In-Memory Cache:**
   * Maintain `Dictionary<int, string> _recapCache`.
   * If `oldPage` already has a cached recap, update `RecapWindow` **instantly (0 ms)** without calling the AI.
2. **Lightweight LLM Request:**
   * If not cached, extract the text of `oldPage` using `MarkdownNormalizer` / `PageSummarizer.ExtractRangeAsync`.
   * Send an OpenAI-compatible request using `AiProviderConfig`:
     ```text
     Condense the following text into a single short paragraph of 3-4 sentences.
     Capture only the essential points — the most important facts, findings, events, or takeaways.
     Drop all detail, examples, and elaboration.
     Write it as flowing prose, not bullets.

     Respond in {language} only.

     Text to condense:
     {pageText}
     ```
   * Low temperature (`0`), fast single-pass response.
   * Cache the resulting text in `_recapCache[oldPage]`.

---

### 5. Localization Parity
Add all required keys across `Strings/en-US.xaml` and all other 15 language dictionaries:
* `Str_Lbl_RecapMode`: `"Recap"`
* `Str_TT_RecapMode`: `"Toggle automatic recap companion when turning pages"`
* `Str_Recap_Title`: `"Recap — Page {0}"`
* `Str_Recap_RangeTitle`: `"Recap — Pages {0}–{1}"`
* `Str_Recap_Loading`: `"Condensing page…"`
* `Str_Recap_Empty`: `"No readable text on this page to recap."`

---

### Verification
1. Run `dotnet build` with zero errors.
2. Run `dotnet test` and ensure all tests pass (including localization parity).
3. Open `SummaryWindow`: verify the Recap toggle switch sits in the top middle bar across from "Avalanche" and the AI Tester button.
4. Toggle Recap ON, read a PDF, and navigate from Page 1 to Page 2:
   * Verify `RecapWindow` opens dressed like `SummaryWindow` with title `"Recap — Page 1"`, displaying the 3–4 sentence condensation without any control bars.
   * Navigate to Page 3: verify the window updates to Page 2 seamlessly.
   * Navigate back to Page 2: verify it loads instantly from cache (0 ms).
   * Toggle Recap OFF: verify the window closes.
