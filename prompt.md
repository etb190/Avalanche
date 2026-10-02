# TASK: Implement Native Token-Efficient Markdown Normalizer & Replace Outlines with "Notes" Sidebar Tab

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### PART 1: Universal Native Markdown Normalizer (For Summary, AI Test, SideChat & Notes)

#### 1. The Problem
Currently, text extracted from PDFs across Avalanche uses raw visual line-by-line dumps:
* Visual line wraps: Every visual line on the printed page ends with a hard `\n`, breaking sentences into fragmented pieces and wasting thousands of whitespace tokens.
* Hyphenated word splits: Margin hyphens (e.g. `civili- \n zation`) waste 3–4 tokens per word instead of 1 token for `civilization`.
* Custom markup overhead: Wrapping headings in `[[H]] Title [[/H]]` consumes 6–8 punctuation tokens per header.
* On a 100-page book (such as *Ancient Mesopotamia*), this wastes **10,000 to 15,000 tokens** (~15% of the total budget), increases latency, and degrades the LLM's natural reading flow.

#### 2. The Solution: Geometric Markdown Normalizer in `PageSummarizer.ExtractRangeAsync`
Upgrade the extraction pipeline in `Features/Summary/PageSummarizer.cs` (and expose it for `DocumentChunker` / `SideChat`) to produce clean, compact, token-dense **GitHub-Flavored Markdown**:

1. **De-Hyphenation:**
   If a visual line ends with a letter followed by a hyphen (`-`) and the next line begins with a lowercase letter, strip the hyphen and merge the word directly without a space (`devel-` + `opment` $\rightarrow$ `development`).
2. **Paragraph Reflow:**
   If a line does not end with sentence-ending punctuation (`.`, `!`, `?`, `:`) or if the vertical gap between lines is regular line-spacing, join the lines with a single space ` ` instead of a newline `\n`. Only emit a paragraph break (`\n\n`) when:
   - The line ends with sentence punctuation **and** vertical distance exceeds normal leading ($> 1.3\times$ line height), OR
   - The next line begins with an indent or a Markdown heading/bullet.
3. **Native Markdown Headings (Replaces `[[H]]`):**
   Use font size geometry relative to `MedianBodyPointSize`:
   - Title / Chapter: $\text{Font} \ge \text{Median} \times 1.50 \rightarrow$ `# Heading`
   - Section: $\text{Font} \ge \text{Median} \times 1.25 \rightarrow$ `## Heading`
   - Subsection: $\text{Font} \ge \text{Median} \times 1.15 \rightarrow$ `### Heading`
4. **List & Bullet Normalization:**
   Convert PDF bullet glyphs (`•`, `–`, `▪`, `*`) to `- item`. When a bullet item wraps across multiple lines, keep it as part of the same list item rather than breaking into separate paragraphs.
5. **Compact Page Delimiters:**
   Use minimal `[p. N]` anchors. They consume only 3 tokens and provide clear landmark references for the model.
6. **Universal Compatibility Across All 4 AI Features:**
   * **PageSummarizer (Single-pass & Fusion):** Summarizer prompts ingest clean Markdown headers and reflowed paragraphs; update the heading preservation regex to recognize `#` / `##` alongside any legacy markers.
   * **AiContextTester (AI Test Probe):** Update marker stripping and boundary extraction in `AiContextTester.cs` to handle both `[p. N]` and legacy `[[p. N]]`.
   * **SideChat (AI Chat Assistant):** Ensure `DocumentChunker.cs` chunks from the normalized Markdown text so RAG retrieval embeddings and context windows are 15% denser and free of hyphenation artifacts.
   * **Notes (New Feature):** Consumes the exact same Markdown extraction for its 50-page chunked processing.

---

### PART 2: Replace "Outlines" with "Notes" Sidebar Tab (50-Page Chunked Recall Digest)

#### 1. Overview & Objective
In the left sidebar of `MainWindow.xaml`, replace the existing **Outlines** tab with **Notes**.

The **Notes** panel allows the reader to input an arbitrary page range (e.g., `From: 80` to `To: 200`) and produces dense, comprehensive, high-retention review notes broken into **50-page chunks (~200 words per chunk)**. This is designed for rapid recall and review of what was already read, maintaining maximum density and zero fluff.

#### 2. UI Replacement in `MainWindow.xaml` (`Sidebar` Section)

1. **Tab Strip Header:**
   - In `MainWindow.xaml` (around line 1829), replace `SidebarOutlinesTab` with `SidebarNotesTab`:
     - Text: Localized string `Str_TabNotes` ("Notes").
     - Retain identical tab button styling matching `SidebarPagesTab`.
2. **Body Replacement:**
   - Replace `OutlineScrollViewer` / `OutlineTree` with a dedicated `NotesScrollViewer` and `NotesPanel`:
   - **Controls Header (Pinned at top of Notes tab):**
     - Inputs: `From` page textbox and `To` page textbox (numeric validation, defaulting to `1` and `totalPages` or the currently viewed page range).
     - Action Button: `"Generate Notes"` (`AccentButton` style).
     - Global Action: `"Copy All Notes"` icon button (`⧉`).
   - **Notes Card List (Scrollable):**
     - Dynamic stack of note cards for each 50-page interval.
     - Each card includes:
       - Header: `Pages {start} – {end}` with an individual Copy button (`⧉`).
       - Content area rendering the formatted markdown note (~200 words).
       - Empty state when no notes have been generated yet.
       - Loading indicator with progress text while generating.

#### 3. Slicing & Boundary-Aware LLM Strategy (128k Single-Pass)

When reading Pages 80 to 200, an argument or narrative event that starts on Page 129 may finish on Page 130. Slicing blindly into isolated requests creates dangling fragments and orphaned thoughts.

Because `gpt-oss:120b-cloud` supports a **128k context window**:
1. **Send the full continuous text of the requested range (e.g. Pages 80–200)** to the model in **one single prompt**.
2. Because the model sees the entire text seamlessly, it has full continuity across all page transitions and never gets confused by sentence boundaries.
3. The prompt explicitly instructs the model to return structured output broken into **discrete 50-page chunks**:
   * Chunk 1: Pages 80 – 129 (50 pages $\rightarrow$ ~200 words)
   * Chunk 2: Pages 130 – 179 (50 pages $\rightarrow$ ~200 words)
   * Chunk 3: Pages 180 – 200 (remaining pages $\rightarrow$ proportional word count)

#### 4. Prompt Design: The 200-Word Recall Schema

For each 50-page chunk, the model must produce a dense, high-yield ~200-word card following this 3-tier structure:

```text
You are a master analytical reader creating high-yield, comprehensive review notes.
For each 50-page block, generate a dense note of STRICTLY under 200 words following this exact structure:

1. CORE ARC (30–40 words):
   The central premise, thesis, or primary narrative shift across these 50 pages.
2. CHRONOLOGICAL PROGRESSION (120–130 words):
   The sequence of ideas, events, and evidence, anchored by page milestones:
   - [pp. X-Y] ...
   - [pp. Y-Z] ...
   - [pp. Z-End] ...
3. MEMORY PEGS & SPECIFICS (30–40 words):
   The 2–3 most distinct specifics that anchor memory: exact names, central analogies, key case studies, formulas, or pivotal counterarguments.

RULES:
- Be dense, concrete, and substantive. Do not use generic filler ("the author discusses", "this section covers").
- State the actual arguments, findings, and events directly.
- Strictly adhere to the word ceiling per card.
```

#### 5. Implementation Details in `Features/Notes/`
1. Create a service `NotesGenerator.cs`:
   - Extracts the requested page span using the Markdown normalizer.
   - Calculates the 50-page chunk boundaries.
   - Builds the OpenAI-compatible `/chat/completions` request against `AiProviderConfig`.
   - Parses the model's response into individual card items.
2. Copy button interactions:
   - Individual card copy button copies `**Pages X-Y**\n\n{content}` to clipboard with temporary `✓` feedback.
   - Global copy button copies all generated cards concatenated.
3. Add required localized strings across `Strings/en-US.xaml` and all other 15 language dictionaries:
   - `Str_TabNotes`: `"Notes"`
   - `Str_Notes_From`: `"From"`
   - `Str_Notes_To`: `"To"`
   - `Str_Notes_Generate`: `"Generate Notes"`
   - `Str_Notes_CopyAll`: `"Copy All Notes"`
   - `Str_Notes_Empty`: `"Select a page range and click Generate Notes to create review cards."`
   - `Str_Notes_Generating`: `"Generating notes for pages {0} to {1}…"`

---

### Verification
1. Run `dotnet build` with zero errors.
2. Run `dotnet test` and ensure all tests (including localization parity) pass.
3. Test Markdown extraction: verify paragraphs flow continuously, words are de-hyphenated, and headers use `#`/`##`.
4. Test with a PDF: verify entering `80` to `200` generates 3 distinct, copyable ~200-word cards in the sidebar.
