# TASK: Replace Outlines with "Notes" Sidebar Tab (50-Page Chunked Digest)

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### Overview & Objective
In the left sidebar of `MainWindow.xaml`, replace the existing **Outlines** tab with a new feature called **Notes**.

The **Notes** panel allows the reader to input an arbitrary page range (e.g., `From: 80` to `To: 200`) and generates dense, comprehensive, high-retention notes broken into **50-page chunks (~200 words per chunk)**. This is designed for rapid recall and review of what was already read, maintaining high density and zero fluff.

---

### 1. UI Replacement in `MainWindow.xaml` (`Sidebar` Section)

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

---

### 2. Slicing & Boundary-Aware LLM Strategy

#### Solving Boundary Cutoffs with 128k Context
When reading Pages 80 to 200, an argument or narrative event that starts on Page 129 may finish on Page 130. Slicing blindly into isolated requests creates dangling fragments and orphaned thoughts.

Because `gpt-oss:120b-cloud` supports a **128k context window**:
1. **Send the full continuous text of the requested range (e.g. Pages 80–200)** to the model in **one single prompt**.
2. Because the model sees the entire text seamlessly, it has full continuity across all page transitions and never gets confused by sentence boundaries.
3. The prompt explicitly instructs the model to return structured output broken into **discrete 50-page chunks**:
   * Chunk 1: Pages 80 – 129 (50 pages $\rightarrow$ ~200 words)
   * Chunk 2: Pages 130 – 179 (50 pages $\rightarrow$ ~200 words)
   * Chunk 3: Pages 180 – 200 (remaining pages $\rightarrow$ proportional word count)

---

### 3. Prompt Design: The 200-Word Recall Schema

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

---

### 4. Implementation Details in `Features/Notes/` or `Features/Summary/`

1. Create a service / handler `NotesGenerator.cs` (or extend existing AI services):
   - Extracts the requested page span using `TextRunService` (with `[[p. N]]` markers).
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
3. Test with a PDF: verify entering `80` to `200` generates 3 distinct, copyable ~200-word cards in the sidebar.
