# TASK: Summary Polish, Sidebar Notes UI, AI Tester Layout & Reasoning Token Fix

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### 1. Notes Prompt Refinement: Remove Word Count Annotations
In the Notes generation prompt (in `NotesGenerator.cs` / `PageSummarizer.cs`):
* Do **NOT** output word count labels or count annotations like `(37 words)`, `(125 words)`, or `(35 words)`.
* Output clean markdown headers directly with the substantive text:
  ```markdown
  **CORE ARC:**
  [Text directly here...]

  **CHRONOLOGICAL PROGRESSION:**
  - **[pp. 1–6]** ...
  - **[pp. 7–14]** ...

  **MEMORY PEGS & SPECIFICS:**
  [Key pegs directly here...]
  ```
* Retain honest front-matter / copyright reporting if present in the text (the user specifically wants to verify that the model begins at the true start of the book).

---

### 2. Sidebar Tab Toggle (Pages vs. Notes): Bolder, Wider Green Buttons
In `MainWindow.xaml` (`Sidebar` header):
* Upgrade `SidebarPagesTab` and `SidebarNotesTab` to be larger and bolder.
* Style them as wide, high-contrast segmented control buttons with green accent styling:
  * **Active Tab:** Solid green accent container (`#1B5E20` or theme `AccentBrush`), bold white/bright text, clear visual depth.
  * **Inactive Tab:** Subtle muted container with clean border, slightly dimmer text.
* Ensure clicking instantly toggles visual states so the reader immediately knows which tab is active.

---

### 3. Summary Window: "Reset" Clears Text Only (Preserve Range)
In `Features/Summary/SummaryWindow.xaml.cs`:
* Clicking **Reset** must **NOT** reset the page range back to Page 1.
* Reset must simply clear the generated summary text and reset the window state to idle/ready.
* Keep the currently selected page range intact and wait for the user to click Start.

---

### 4. Summary Window: Background 30-Second Prefetch Buffer
In `Features/Summary/SummaryWindow.xaml.cs`:
* Implement a 1-step ahead prefetch buffer for smooth reading:
  * When a summary finishes generating, start a 30-second delay timer.
  * After 30 seconds (while the user is reading the current summary), trigger a background prefetch for the **next sequential range** (e.g. if Pages 1–20 was just completed, prefetch Pages 21–40 in the background into an in-memory buffer `_nextRangeBuffer`).
  * When the user clicks **"Next" / "Next Range"**:
    * If the buffer is ready: Display it instantly (0 ms lag).
    * If it is still in flight: Seamlessly attach to the running task and show the progress indicator.
  * If the user manually changes the range or navigates elsewhere, cancel and invalidate the buffer.

---

### 5. Summary Window: Default Start Page to Current PDF Page
In `MainWindow.xaml.cs` (when launching `SummaryWindow`):
* When opening the summary navigator, the default start page of the range should be the **current active page the user is viewing in the PDF editor**, rather than always starting at Page 1 (unless manually changed in the range controls).

---

### 6. AI Tester: Pin Mode Toggle to Far Left with Vertical Separator Border
In `Features/AI/AiTestWindow.xaml`:
* The mode toggle (`Pages` vs. `Range`) must be pinned permanently to the **far left** of the controls row and **never change position** or shift when toggled.
* Add a vertical right border (`BorderThickness="0,0,1,0"`, themed hairline border brush) immediately to the right of the toggle container to cleanly separate it from the preset chips, custom range inputs, and Run button.

---

### 7. AI Tester: Persist Mode, Range & Coordinates
In `Features/AI/AiTestWindow.xaml.cs`:
* Ensure the following settings are saved to and restored from user settings:
  1. Selected mode: `Pages` mode vs. `Range` mode (`aitest.mode`).
  2. Last entered custom page range string (e.g. `84-120`) (`aitest.range`).
  3. Window position and dimensions across sessions.

---

### 8. AI Tester: Auto-Select Text in Range Input on Focus
In `Features/AI/AiTestWindow.xaml.cs`:
* Attach `GotFocus` and `PreviewMouseLeftButtonDown` handlers to the custom range `TextBox`:
* Automatically call `SelectAll()` when the box is clicked or focused so typing immediately replaces the numbers without having to backspace first (matching the behavior of the range input in `SummaryWindow`).

---

### 9. Summary Window Title Bar: Spacing of AI Test Button
In `Features/Summary/SummaryWindow.xaml`:
* Adjust the title bar button layout so the beaker/test icon button sits closer to the minus (`-`) zoom button.
* The gap between the AI Test button and the minus (`-`) button must exactly match the gap between the minus (`-`) and plus (`+`) zoom buttons.

---

### 10. Notes Sidebar: Match Dimensions with Summary Window & Visual Polish
In `MainWindow.xaml` (`NotesPanel`):
* **Inputs (`From` and `To`):** Make them less wide and increase their height to match the exact dimensions of the range input box in `SummaryWindow`.
* **Generate Notes Button:** Match the height and styling of the primary action buttons in `SummaryWindow`.
* **Copy Buttons:** Use a richer, more vibrant green (`#2E7D32` or theme green accent).
* **Separator Border:** Place a clean horizontal separator border between the top controls group and the scrollable notes card list below.

---

### 11. Fix the 66% "Ran Out of Budget on Hidden Reasoning" Error in `AiContextTester`

#### Deep Root-Cause Analysis
When testing large documents (e.g. 80,000+ tokens / ~320,000 characters):
1. **Reasoning Models Exhaust `max_tokens`:** Models like `gpt-oss:120b-cloud`, `deepseek-r1`, or `o3` spend internal deliberation tokens attempting to "read" the entire 80k text before emitting output.
2. **Ceiling Cutoff:** `BuildProbeRequest` sets `max_tokens` to `8192`. Once internal reasoning hits 8,192 tokens, generation halts with `finish_reason: "length"`, leaving `message.content` empty (`""` or `null`).
3. **Empty Content Check:** `AiContextTester.cs` line 188 sees `content` is empty/whitespace and throws the fault: *"the model returned no answer (its whole budget may have gone to hidden reasoning)"*.
4. **Content in `reasoning_content`:** In some OpenAI-compatible bridges, the model actually wrote its answer or JSON at the end of `reasoning_content`, but `ExtractReplyContent` only inspects `message.content`.

#### The Fix in `AiContextTester.cs` & `AiProbeLogic.cs`:
1. **Suppress Unnecessary Reasoning via Prompt:**
   In `ProbeSystemPrompt`, add an explicit reasoning guardrail:
   ```text
   CRITICAL: Do NOT write extensive internal thinking, analysis, or deliberation.
   Immediately locate the first sentence and last sentence of the text and output ONLY the JSON object.
   ```
2. **Increase Output Budget & Pass `max_completion_tokens`:**
   In `BuildProbeRequest`:
   * Increase the token limit to `16384`:
     ```csharp
     ["max_tokens"] = Math.Max(config.MaxTokens, 16384),
     ["max_completion_tokens"] = Math.Max(config.MaxTokens, 16384),
     ```
   * Pass `"options": { "num_predict": 16384 }` for native Ollama compatibility.
3. **Fallback Extraction from `reasoning_content`:**
   In `AiProbeLogic.ExtractReplyContent(string json)`:
   * If `message.content` is null or empty, check if `message.reasoning_content` (or `message.reasoning`) exists and is non-empty.
   * If found, extract and return the content from `reasoning_content`.
   * Also ensure `ExtractJsonBlock()` can extract `{...}` from reasoning text containing `<think>` tags so that even if the bridge routes text through reasoning, the test parses the JSON and passes seamlessly.

---

### Verification
1. Run `dotnet build` with zero errors.
2. Run `dotnet test` and ensure all tests (including localization parity) pass.
3. Test AI Tester: run a test on a 100-page document — verify that the reasoning timeout error is eliminated and returns a valid verdict.
4. Test Notes sidebar: verify no word count labels appear and the UI matches `SummaryWindow` styling.
