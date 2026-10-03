# TASK: Summary Navigator Interactive Floating Actions (Search, Define, and Range-Grounded Explain)

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### Overview & Core Purpose
Inside Avalanche's Summary Navigator (`SummaryWindow`), reading digests can contain unfamiliar terminology, historical figures, foreign phrases, or dense theoretical assertions.
Currently, the reader must leave the application or open external tools to look up terms or figure out what the author was talking about in the original pages.

Implement an interactive floating action popup on the digest text (`DocBox`):
1. **Interactive Words**: Every word in the summary is clickable to pop up 3 action buttons.
2. **Multi-Word Shift Selection**: Highlighting any phrase/sentence and pressing `Shift` (or selecting while holding Shift) anchors the 3 action buttons over the entire selection.
3. **The 3 Actions**:
   - **Search**: Opens the search query in the default system web browser (same as `pdf-summarizer-extension`).
   - **Define**: Generates a concise, high-precision definition for words/terms.
   - **Explain (Source-Grounded)**: **Takes the actual raw source pages into context, NOT the summary!** Retrieves the unabridged Markdown text for the active page range (`_runFirstPage.._runLastPage`) via `PageSummarizer.ExtractRangeAsync` and prompts the AI to explain the selected passage grounded directly in the author's original pages.

---

### 1. Hit-Testing & Shift Selection (`SummaryWindow.xaml.cs`)

Use WPF native `TextPointer` hit-testing on `DocBox` (`RichTextBox`) rather than wrapping thousands of words into `InlineUIContainer` or `Span` elements (preserving standard Windows text selection, clipboard copy, and high-speed rendering):

#### A. Single Word Click
* In `DocBox.PreviewMouseLeftButtonUp`:
  * If `DocBox.Selection.IsEmpty` (the user clicked without dragging a selection):
    * Obtain `Point pt = e.GetPosition(DocBox)`.
    * Obtain `TextPointer? tp = DocBox.GetPositionFromPoint(pt, snapToText: true)`.
    * If `tp` is within text, scan backward and forward to whitespace/punctuation boundaries to isolate the clicked word.
    * Obtain the word's bounding rectangle via `startPointer.GetCharacterRect(LogicalDirection.Forward)`.
    * Open the floating action popup anchored directly above the target word (or below if near the top viewport).

#### B. Multi-Word Shift Selection
* In `DocBox.PreviewKeyDown` and `DocBox.PreviewMouseLeftButtonUp`:
  * When `!DocBox.Selection.IsEmpty`:
    * If the reader presses `Key.LeftShift` or `Key.RightShift`, or completes a mouse selection with `Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)`:
      * Grab `DocBox.Selection.Text.Trim()`.
      * Obtain bounding rectangle from `DocBox.Selection.Start.GetCharacterRect(LogicalDirection.Forward)`.
      * Open the floating action popup anchored to the highlighted selection.

#### C. Auto-Dismissal & Keyboard Handling
* Dismiss the action popup when:
  * The user clicks anywhere outside the popup.
  * The user scrolls `DocBox` (`ScrollChanged` event).
  * The user presses `Escape`.
  * The range stepper or summary generation resets.

---

### 2. Floating Action Popup UI

Create a dedicated floating popup (`SummaryActionPopup` or integrated `System.Windows.Controls.Primitives.Popup` in `SummaryWindow`):
* **Styling**:
  * Dark glass aesthetic matching Avalanche (`#1E1E24` background, `CardBorderBrush` 1px border, rounded corners, subtle drop shadow, `AllowsTransparency="True"`).
  * Smooth entry animation via `WindowFx.PlayOpenPop` or WPF opacity fade.
* **Header Bar**:
  * Displays the target word or truncated excerpt.
  * Small close chip (`[x]`) to dismiss.
* **Action Buttons Row**:
  * `[Search]`: Icon (`&#xE721;` MDL2 Search) + "Search".
  * `[Define]`: Icon (`&#xE82D;` MDL2 Book/Dictionary) + "Define" (primarily for words/terms).
  * `[Explain]`: Icon (`&#xE946;` MDL2 Info / Lightbulb) + "Explain" (accent color to emphasize importance).
* **Collapsible Result Card**:
  * Initially collapsed.
  * When `Define` or `Explain` is clicked:
    * Card smoothly expands below the buttons.
    * Shows animated loading indicator (`Str_SummaryGenerating` / spinner).
    * Action buttons disabled during request; `CancellationTokenSource` wired to cancel if dismissed.
    * Renders AI markdown output cleanly via `AiMarkdown` with selectable text and copy button.

---

### 3. Execution of the 3 Actions

#### 1. Search Action
* When clicked:
  * Clean the selected text.
  * Launch the system default web browser:
    ```csharp
    string query = Uri.EscapeDataString(cleanText);
    string url = $"https://www.google.com/search?q={query}";
    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    ```

#### 2. Define Action (Lexical & Nuance)
* Fast, focused lexical definition prompt:
  ```markdown
  You are an authoritative, concise dictionary assistant.
  The reader is studying a text in ${Language} and highlighted the term: "${word}".

  Define "${word}" concisely in English (1-3 sentences):
  1. Part of speech and core literal definition.
  2. If the term is non-English, provide its exact English translation first.
  3. Any cultural, technical, or contextual nuance in how the term is used.
  Do NOT include conversational meta-talk or introductory pleasantries.
  ```

#### 3. Explain Action (Range-Grounded Source Analysis - CORE REQUIREMENT)
* **The Crucial Distinction**:
  * Traditional summarizers explain words using only the summary snippet or general training knowledge.
  * Avalanche's `Explain` **takes the whole page range into context** using the unabridged raw book text!
* **Pipeline**:
  1. In `SummaryWindow`, keep a cached reference to the raw range Markdown string (`_cachedRangeRawText`) returned during the active summary pass:
     `PageSummarizer.ExtractRangeAsync(_filePath, _runFirstPage, _runLastPage, ct)`.
     *(If cache is empty, fetch it asynchronously via `ExtractRangeAsync`).*
  2. Call `PageSummarizer.ExplainExcerptAsync(...)`:
     ```markdown
     You are a scholarly reading companion.
     The reader is studying a book and highlighted the following passage/term:
     "${selectedText}"

     Below is the UNABRIDGED RAW SOURCE TEXT from pages ${firstPage} to ${lastPage} of the book:
     --- BEGIN SOURCE TEXT ---
     ${rawRangeText}
     --- END SOURCE TEXT ---

     TASK:
     Explain "${selectedText}" in depth, grounded STRICTLY in the source text above:
     1. **Source Context:** Locate where and how this appears in the source pages. Cite specific page numbers [p. N] and quote surrounding context where helpful.
     2. **Author's Meaning:** Explain what the author specifically means by this term/passage in the context of their argument, historical evidence, or narrative scene.
     3. **Omitted Nuance:** Highlight any specific details, derivations, dialogue, or caveats present in the original pages that are omitted from high-level summaries.

     RULES:
     - Base your answer directly on the provided raw source text.
     - Write in clear, dense prose with bold key concepts.
     ```

---

### 4. Localization Parity (16 Languages)
Add localized strings across all 16 `Strings/*.xaml` resource dictionaries:
* `Str_SummaryPopupSearch`: "Search"
* `Str_SummaryPopupSearchTT`: "Search the web for this selection in your default browser"
* `Str_SummaryPopupDefine`: "Define"
* `Str_SummaryPopupDefineTT`: "Get a concise definition of this word"
* `Str_SummaryPopupExplain`: "Explain"
* `Str_SummaryPopupExplainTT`: "Explain this selection using the full page range context"
* `Str_SummaryPopupDefining`: "Defining..."
* `Str_SummaryPopupExplaining`: "Analyzing source pages..."
* `Str_SummaryPopupError`: "Unable to load explanation."

---

### 5. Verification & Acceptance Checklist
1. **Word Click**:
   - Clicking any word in `DocBox` brings up the 3 buttons directly anchored to that word.
   - Clicking outside or scrolling dismisses the popup cleanly.
2. **Shift Selection**:
   - Selecting a phrase and pressing `Shift` (or Shift-dragging a selection) anchors the popup over the full selected passage.
3. **Search Verification**:
   - Clicking Search opens the default browser with the exact encoded query.
4. **Define Verification**:
   - Clicking Define displays a concise 1-3 sentence definition inside the card.
5. **Explain Verification**:
   - Clicking Explain passes the unabridged raw page text of `_runFirstPage.._runLastPage` to the AI.
   - The explanation cites the actual pages (`[p. N]`) and explains the concept using the author's real source context rather than generic summary text.
6. **Performance & Stability**:
   - Zero UI thread freezing during network requests.
   - Caching avoids redundant PDF extractions.
   - All existing 2,004 unit tests continue to pass (`dotnet test`).
