# TASK: Implement "Recap" Mode in Avalanche (Replicating pdf-summarizer-extension)

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### Overview & Objective
Implement the **Recap** feature in Avalanche, faithfully replicating the exact behavior, navigation trigger mechanism, AI condensation prompt, and floating popup UI from `pdf-summarizer-extension`.

Recap is an automatic page-turn memory refresher: when **Recap Mode** is enabled, navigating to a new page (`Page 10` $\rightarrow$ `Page 11`) automatically opens a floating, draggable, resizable card showing a dense **3–4 sentence recap of what happened on the page just left (`Page 10`)**, keeping the immediate context fresh as the reader begins the next page.

---

### 1. Header / Toolbar Toggle: "Recap Mode"
* Add a `Recap` toggle button/checkbox to the toolbar or viewer header (beside the AI controls):
  * Label: `Recap` (localized key: `Str_Lbl_RecapMode`).
  * Tooltip: `Show a quick recap popup of the previous pages on navigation.` (`Str_TT_RecapMode`).
  * State: Boolean `recapMode`, persisted in user settings (`recap.enabled`).
  * When toggled OFF, immediately dismisses any open recap popup.

---

### 2. Navigation Trigger Mechanism
In the document viewer navigation handler (e.g. `OnPageChanged(int oldPage, int newPage)`):
* If `recapMode` is true AND `oldPage != newPage`:
  * Trigger `ShowRecapPopup(oldPage)` for the page the reader just left.
  * If a recap popup is already open from a previous turn, dismiss/replace it cleanly.

---

### 3. AI Condensation Pipeline (`Features/AI/` or `Features/Summary/`)

1. **Text Source:**
   * Uses the text or summary of `oldPage` (extracted via `PageSummarizer.ExtractRangeAsync(path, oldPage, oldPage, ct)` or `MarkdownNormalizer`).
2. **In-Memory Cache:**
   * Maintain a `Dictionary<int, string> _recapCache`.
   * If `oldPage` already has a cached recap, display it **instantly (0 ms)** without calling the AI.
3. **The LLM Request:**
   * If not cached, send an OpenAI-compatible completion request using `AiProviderConfig`:
   ```text
   Condense the following text into a single short paragraph of 3-4 sentences.
   Capture only the essential points — the most important facts, findings, events, or takeaways.
   Drop all detail, examples, and elaboration.
   Write it as flowing prose, not bullets.

   Respond in {language} only.

   Text to condense:
   {pageText}
   ```
   * Low temperature (`0`), fast single-pass completion.
   * Cache the resulting 3–4 sentence string in `_recapCache[oldPage]`.

---

### 4. Floating Popup Window / Overlay (`RecapPopupWindow.xaml`)

Style and behavior replicating `.pn-recap-popup` from `pdf-summarizer-extension`:

1. **Visual Styling:**
   * Floating card with rounded corners (`CornerRadius="12"`), dark glass background (`#0F0F14` / theme `PaneBrush` with backdrop blur), subtle hairline border (`BorderBrush="{DynamicResource ControlBorderBrush}"` or accent tint), and drop shadow.
   * Pop-in entrance animation (0.3s cubic ease-out scale & fade).
2. **Header Bar:**
   * **Title:** Bold uppercase title: `RECAP — PAGE {oldPage}` (or `RECAP — PAGES {start}–{end}` if multi-page).
   * **Font Size Controls:**
     * `A-` button: Decreases recap body font size by 1px (minimum 10px).
     * `A+` button: Increases recap body font size by 1px (maximum 22px).
     * Persist `recap.fontsize` in user settings.
   * **Close Button:** `✕` icon button that dismisses the popup.
3. **Draggable & Resizable:**
   * The header acts as a drag handle (clicking and dragging moves the popup smoothly around the window).
   * Resizable via edge/corner grips.
   * Persist last position and dimensions (`recap.left`, `recap.top`, `recap.width`, `recap.height`) so it reopens where the user placed it.
4. **Body Area:**
   * Shows a smooth loading spinner while generating: `"Condensing page…"`
   * Renders the 3–4 sentence recap with comfortable reading line height (1.6x) and clean typography.
   * Internal thin scrollbar for long text.
5. **Dismissal:**
   * Closes on clicking `✕`, pressing `Escape`, or turning Recap mode OFF.

---

### 5. Localization Parity
Add all required keys across `Strings/en-US.xaml` and all other 15 language dictionaries:
* `Str_Lbl_RecapMode`: `"Recap"`
* `Str_TT_RecapMode`: `"Show a quick recap popup of the previous pages on navigation."`
* `Str_Recap_Title`: `"Recap — Page {0}"`
* `Str_Recap_RangeTitle`: `"Recap — Pages {0}–{1}"`
* `Str_Recap_Loading`: `"Condensing page…"`
* `Str_Recap_Empty`: `"No readable text on this page to recap."`

---

### Verification
1. Run `dotnet build` with zero errors.
2. Run `dotnet test` and ensure all tests (including localization parity) pass.
3. Open a PDF, toggle `Recap` ON, and advance from Page 1 to Page 2:
   * Verify the floating recap card pops in with `"Recap — Page 1"`.
   * Verify font controls (`A-` / `A+`) resize the text.
   * Verify dragging and close (`✕` / `Escape`) work smoothly.
   * Flip back to Page 1 and forward again: verify the recap loads instantly from cache (0 ms).
