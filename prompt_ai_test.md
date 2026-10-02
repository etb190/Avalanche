# TASK: Upgrade Summary Quality & Implement "AI Test" Context Verification System in Avalanche

We have two tasks in Avalanche (`Features/Summary/`, `Features/AI/`, `MainWindow.xaml`):
1. Upgrade the Non-Fiction summary prompt in `PageSummarizer.cs` with the extension's deep detail checklist (preserving analogies, counterarguments, and named case studies).
2. Build an **"AI Test"** verification system: a toolbar button right after "AI Chat" that opens an `AiTestWindow` (styled identically to `SummaryWindow` using `DialogChrome`) which probes the model's actual working context length, verifies boundary sentences, and audits Ollama's `prompt_eval_count` to mathematically prove that no pages are silently truncated.

---

### Part 1: Upgrade the Summary Prompt (`Features/Summary/PageSummarizer.cs`)

In `PageSummarizer.cs`, `DigestSystemPrompt()` currently uses a generic instruction: *"Cover the core arguments, evidence, definitions, historical facts, and conclusions"*. In non-fiction books, this causes models to drop the author's analogies, debate positions, and specific named examples.

Update `DigestSystemPrompt()` to include the **"WHAT COUNTS AS DETAILS"** checklist from `pdf-summarizer-extension/background.js`:

```text
WHAT COUNTS AS "DETAILS" — ALL of these must be covered:
- Facts: names, dates, numbers, places, definitions — reproduced exactly.
- Arguments: the author's claims, thesis, and core reasoning.
- Evidence: the data, studies, examples, and cases the author uses to support claims.
- Interpretations: how the author reads the evidence — what they argue it means.
- Comparisons: EVERY comparison the author makes. If the author compares X to Y, your summary must include that comparison.
- Parallels and analogies: EVERY parallel or analogy the author draws (historical, biological, or cross-cultural analogies). These are often the author's primary explanatory tools — NEVER omit them.
- Counterarguments: positions the author disagrees with, debates, and disproves.
- Specific artifacts, sites, and case studies: every named artifact, site, experiment, courtroom trial, or specific case study. If the author names it, it must appear in the summary.

FORBIDDEN:
- Dropping the author's argument and keeping only the raw facts.
- Merging multiple distinct arguments, comparisons, or parallels into one vague sentence.
- Replacing a specific parallel or case study with a generic statement like "the author draws parallels" or "studies demonstrate".
```

Ensure this is added to both the `DigestSystemPrompt` (single-pass) and the fusion prompt.

---

### Part 2: The "AI Test" Toolbar Button & Window

#### 1. Add "AI Test" Button in `MainWindow.xaml`
In `MainWindow.xaml`, immediately following `AiChatBtn` (around line 1636):
```xaml
<Button x:Name="AiTestBtn" Content="&#xE9D9;" Style="{StaticResource ToolbarButton}" 
        Click="AiTestBtn_Click" ToolTip="{DynamicResource Str_TT_AiTest}"/>
```
*(Also add to the overflow menu alongside `MiAiChat` if applicable).*

Add the localized string in `Strings/en-US.xaml` and all other language dictionaries:
* `Str_TT_AiTest`: `"AI Context Test"`
* `Str_Lbl_AiTest`: `"AI Test"`

#### 2. Create `AiTestWindow` (`Features/AI/AiTestWindow.xaml`, `AiTestWindow.xaml.cs`)
Create an owned companion window following the exact styling pattern of `SummaryWindow.xaml` (using `DialogChrome.Frame`, rounded card chrome, title bar close button, dark/light theme brushes, hit-testable corner resize grips):

**Controls inside `AiTestWindow`:**
- **Header:** Title *"AI Context & Verification Test"*, current model name (`gpt-oss:120b-cloud`), and active document name.
- **Controls row:**
  - Range span selector (e.g. `20p`, `60p`, `100p`, or `All Pages`).
  - "Run Test" primary button (`AccentButton`).
  - "Cancel" button.
- **Results Card:**
  - **Verdict Banner:** Large color-coded status badge:
    - PASS: Full Context Verified (100% Seen) (Green)
    - FAIL: Text Truncated by Ollama (Red)
    - WARNING: Last Boundary Mismatch (Yellow)
  - **Metrics Grid:**
    - **Tokens Read by AI (`prompt_eval_count`):** Compared directly against estimated tokens sent.
    - **Total Characters Sent:** Raw character count of the tested pages.
    - **Response Time & Speed:** Duration (seconds) and tokens/sec.
  - **Boundary Verification Section:**
    - **First Sentence Expected:** (extracted from Page 1).
    - **First Sentence AI Recalled:** (fuzzy string match score %).
    - **Last Sentence Expected:** (extracted from the last page).
    - **Last Sentence AI Recalled:** (fuzzy string match score %).

---

### Part 3: The Testing Engine (`Features/AI/AiContextTester.cs`)

Create `AiContextTester.cs` to execute the probe:
1. **Extraction:**
   - Extract the text of the selected range using `TextRunService`.
   - Identify the exact **First Sentence** of the first page and **Last Sentence** of the last page.
2. **The Probe Request:**
   - Send the extracted text to Ollama (`http://localhost:11434/v1/chat/completions` or `/api/generate`) with a system prompt:
     ```text
     You are a context verification probe. Read the provided text and output a JSON object:
     {
       "first_sentence_seen": "<exact first sentence of the text>",
       "last_sentence_seen": "<exact last sentence of the text>",
       "first_page_marker": "<e.g. [[p. 1]]>",
       "last_page_marker": "<e.g. [[p. 100]]>"
     }
     Do not summarize. Return ONLY the JSON object.
     ```
3. **Audit Token Usage:**
   - Capture `usage.prompt_tokens` (or `prompt_eval_count` from `/api/generate`).
   - If `prompt_tokens` is significantly lower than the characters sent / 4 (e.g., capped at 2,048 or 4,096 when 60,000 tokens were sent), immediately mark as **FAIL: Ollama context truncated the document**.
4. **Compare Boundaries:**
   - Run fuzzy comparison between the AI's reported first/last sentences and the expected sentences extracted directly from the PDF.
   - If both match >= 80%, and `prompt_tokens` matches the input, the test passes with 100% confidence.

---

### Part 4: Add Verification Badge to `SummaryWindow` Status Bar

In `PageSummarizer.cs`, capture `usage.prompt_tokens` from the response JSON of `RunBufferedPassAsync` and `SolidDigestAsync`.
In `SummaryWindow.xaml.cs`, display this in the bottom status line:
```text
[ ✓ 100/100 Pages Verified ]  |  62,400 tokens read  |  1,024 words
```
If `prompt_tokens` indicates truncation, show:
```text
[ ⚠ Truncated at ~page X ]  |  4,096 tokens read  |  Do not read
```

---

### Part 5: Verification & Build

1. Run `dotnet build` and ensure zero errors or warnings.
2. Verify all localization keys exist across all 16 language dictionaries in `Strings/`.
3. Run `dotnet test` to confirm all existing tests in `Avalanche.Tests` and `Avalanche.Engine.Tests` pass.
4. Launch the app, click the new **"AI Test"** button on the toolbar, run a 100-page probe, and verify the resulting green verdict badge and boundary comparison.
