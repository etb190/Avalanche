# TASK: Upgrade Avalanche Page Summarizer for 80p/100p & Fix Pipeline Bugs

We are upgrading the Page Summary companion in Avalanche (`Features/Summary/`) to support 80-page (`80p`) and 100-page (`100p`) ranges, while fixing critical extraction bugs and leveraging our 128k-token context model (`gpt-oss:120b-cloud`). 

The goal of this summary is to serve as a **genuine, comprehensive substitute for reading the book**. Do not redesign the UI chrome or break existing localization patterns. Read all referenced files before editing.

---

### 1. UI & Vocabulary Updates (`Features/Summary/SummaryWindow.xaml`, `SummaryWindow.xaml.cs`, `Strings/`)

1. **Add 80p and 100p Range Chips:**
   - In `SummaryWindow.xaml.cs`, update `RangeChoices`:
     ```csharp
     private static readonly int[] RangeChoices = { 1, 5, 20, 40, 60, 80, 100 };
     ```
   - In `SummaryWindow.xaml`, add `RangeChip80` (Tag="80") and `RangeChip100` (Tag="100") styled identically to the existing range chips in `RangePanel`.
   - Wire up `RangeChip80` and `RangeChip100` in `SummaryWindow.xaml.cs` alongside the existing chips.
   - Add localization strings `Str_SummaryR80p` ("80 pages" / "80p") and `Str_SummaryR100p` ("100 pages" / "100p") across `Strings/en-US.xaml` and all language dictionary files to pass the localization gate.

2. **Add Higher Word Ceilings for Deep Reading:**
   - A 1,000-word ceiling on 100 pages forces an unreadable 10 words/page. Add larger options to `WordChoices`:
     ```csharp
     private static readonly int[] WordChoices = { 500, 750, 1000, 1500, 2000, 3000, 4500 };
     ```
   - Ensure the dropdown displays these numbers cleanly (e.g., `3,000` and `4,500`).

---

### 2. Leverage 128k Context (Single-Pass Direct Processing)

1. **Eliminate Unnecessary Map-Reduce Slicing:**
   - In `PageSummarizer.cs`, `SinglePassCharBudget` is currently hardcoded to `45000` (~11k tokens) and `SegmentCharBudget` to `30000` (~7.5k tokens).
   - Our configured model (`gpt-oss:120b-cloud`) has a 128k context window. 100 pages of text (~250,000 chars $\approx$ 60,000 tokens) comfortably fits in a single context window.
   - Raise `SinglePassCharBudget` to `300000` (or dynamically check if using `gpt-oss:120b-cloud` / 128k).
   - For ranges up to 100 pages, **run as a single direct pass** rather than slicing into 10 separate segments. This preserves the author's full argumentative arc, eliminates the "telephone game" of summarizing rough notes twice, and reduces generation time from 6 minutes down to ~45 seconds.

---

### 3. Critical Bug Fixes in `Features/Summary/PageSummarizer.cs`

1. **Fix Page Cleaving in `SegmentPages`:**
   - When text does exceed the single-pass budget, `SegmentPages()` currently splits by `rangeText.Split("\n\n")`. Because paragraphs inside a page use `\n\n`, it slices through the middle of pages, causing orphaned paragraphs in the next segment with missing `[[p. N]]` markers.
   - **Fix:** Split strictly by regex on `(?=\[\[p\.\s*\d+\]\])` so segments are partitioned **only on whole page boundaries**, guaranteeing every segment starts with its valid page marker.

2. **Fix False Aborts on Low-Text/Figure Pages:**
   - Currently, if $\ge 30\%$ of pages have $< 20$ letters, or total letters $< 100 \times \text{pages}$, Avalanche aborts the entire run (`Str_SummaryNoTextRange`).
   - In 80–100 page books, front matter, full-page diagrams, charts, and blank chapter plates easily make up 30+ pages.
   - **Fix:** Instead of aborting the whole run, log the low-text pages, omit them from extraction, and proceed with summarizing all content-bearing pages. Only abort if the *entire range* has fewer than 250 letters total.

3. **Fix Running Head Filter Threshold:**
   - `DetectRunningHeads` calculates `threshold = Math.Max(3, (int)Math.Ceiling(pageCount * 0.4));`. For 100 pages, this requires a header to appear on 40 pages to be removed. Chapter headers spanning 20–30 pages are currently not detected and flood the digest as repeated headings.
   - **Fix:** Lower the threshold to detect repeating heads across chapter-length spans (e.g., $\ge 15\%$ or $\ge 5$ pages), or verify against top-of-page line positions.

4. **Improve Prose Continuity in `ProseGuard.cs`:**
   - When bullet flattening occurs, `ProseGuard.ConvertBulletsToProse` simply joins bullet points with periods, creating robotic, disjointed paragraphs.
   - **Fix:** Enhance the prompt and `ConvertBulletsToProse` so that paragraphs flow naturally with proper connective phrasing rather than raw concatenated fragments.

5. **Fault Tolerance for Multi-Segment Runs:**
   - If a multi-segment pass is ever needed and one segment fails after retries, do NOT throw away the entire 8-minute run. Synthesize the successful segments and append a clear note: *"Summary excludes pages X–Y due to provider timeout."*

---

### 4. Verification & Testing

1. Run `dotnet build` and ensure zero errors or warnings.
2. Run all tests in `Avalanche.Tests` (including `SummaryProseGuardTests.cs` and `LocalizationParityTests.cs`).
3. Verify that selecting `80p` and `100p` correctly updates the start/end page span in the summary companion window, and that `gpt-oss:120b-cloud` streams back a dense, flowing, chapter-structured digest without getting cut off.
