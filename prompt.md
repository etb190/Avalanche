# TASK: Fix Boundary Extraction & Verification Logic in "AI Test" (`Features/AI/AiContextTester.cs`)

### Context & Root Cause
In testing with real documents (e.g. *Ancient Mesopotamia*), the AI successfully processed **77,656 tokens** (~321,000 characters) in a single pass (97% of estimated tokens), proving the full context window is active and undamaged. However, the UI reported `WARNING — Last Boundary Mismatch` and `match: 0%` on the first sentence because of four edge cases in `Features/AI/AiContextTester.cs`:

1. **Cover / Blank Front-Matter Pages:**
   `firstExpected` was extracted strictly from `PageBlock(rangeText, firstPage)` (page 1). Many PDFs have a cover image, blank leaf, or title page with no text or letters on page 1. When `PageBlock` returned empty, `firstExpected` became `""` (rendering as `—`).
2. **Title Pages Without Sentence Terminators:**
   Page 2 had book title and subtitle lines without sentence-ending punctuation (`.`, `!`, `?`). The model faithfully followed the system prompt instruction ("everything from the very start of the text up to and including its first sentence-ending punctuation mark") and quoted everything from the start of the book (`[[p. 1]]\n\n[[p. 2]] [[H]] Ancient Mesopotamia [[/H]]...`) until the first punctuation mark on Page 3. Comparing an empty string against the model's recalled text produced a `0%` match.
3. **Unstripped Page Markers in Fuzzy Comparison:**
   `AiProbeLogic.Flatten()` stripped `[[H]]` and `[[/H]]`, but left `[[p. N]]` markers intact. When models include page markers like `[[p. 1]]` or `[[p. 2]]` in their verbatim quote, `Normalize()` converts them to `p1p2`, causing false similarity penalties.
4. **Hardcoded Warning Banner String:**
   `Str_AiTest_Warn` in `Strings/*.xaml` is labeled `"WARNING — Last Boundary Mismatch"`. When the *first* boundary failed to match, it displayed this string, misleading the user into thinking the end of the text was cut off.

---

### Required Changes

#### 1. Robust Boundary Extraction in `AiContextTester.cs`
In `AiContextTester.RunProbeAsync`:
- **First Sentence Extraction:** Do not restrict `firstExpected` to `PageBlock(rangeText, firstPage)`. If `firstPage` has no extractable sentence (blank cover, image, or no punctuation), scan forward through `firstPage .. lastPage` until finding a page with text, or extract the first sentence directly from `rangeText` after stripping page markers.
- **Last Sentence Extraction:** Similarly, if `lastPage` has no text or sentence (e.g. blank back cover or diagram), scan backward from `lastPage .. firstPage` until finding a page with text, or extract the last sentence directly from `rangeText` after stripping page markers.
- **Title / Non-Terminated Fallback in `AiProbeLogic.FirstSentence`:**
  If the text contains no sentence-ending punctuation (`.`, `!`, `?`) within the first 200 characters (e.g. title page headers), do not return an empty string: return the first non-empty line or the first 200 characters of clean prose.

#### 2. Clean Page Markers in `AiProbeLogic.Flatten` & `Normalize`
In `AiProbeLogic.Flatten`:
- Strip `[[p.\s*\d+]]` page markers in addition to `[[/?H]]` heading tags so that neither the expected sentence nor the AI's recalled text contains page marker scaffolding:
  ```csharp
  string clean = System.Text.RegularExpressions.Regex.Replace(pageText ?? string.Empty, @"\[\[p\.\s*\d+\]\]", string.Empty);
  clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\[\[/?H\]\]", string.Empty);
  return System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
  ```
- In `AiProbeLogic.SimilarityPercent`:
  Normalize both `expected` and `recalled` through `Flatten` before stripping non-word characters.
  If one string contains the other (e.g. the AI quoted title + subtitle + first sentence, or the AI quoted a valid sub-phrase), compute similarity taking substring containment into account so valid verbatim recalls score >= 80%.

#### 3. Accurate Warning Banner Status in `AiTestWindow.xaml.cs`
- When `verdict` is a warning, differentiate based on which boundary mismatched:
  - If `firstMatch < 80` and `lastMatch >= 80`: First boundary mismatch.
  - If `lastMatch < 80` and `firstMatch >= 80`: Last boundary mismatch.
  - If both < 80: Boundary mismatch.
- Alternatively, update `Str_AiTest_Warn` in `Strings/*.xaml` to a universal warning: `"WARNING — Boundary Mismatch ({0}% Seen)"` or update the banner text accordingly so it is never inaccurate.

---

### Verification
1. Run `dotnet build` and ensure zero errors or warnings.
2. Run `dotnet test` and ensure all tests pass.
3. Test against a document with an image cover (page 1 blank): verify that `First sentence expected` finds the true opening text and matches the AI recall, resulting in a green `PASS — Full Context Verified` banner.
