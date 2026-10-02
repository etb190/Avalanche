# TASK: Fix AI Test Verification, UI Styling, Custom Range Toggle & Summary Window Polish

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### 1. Fix Boundary Verification in AI Context Tester (`Features/AI/AiContextTester.cs`)

#### The Issue
In our real-world document test (*Ancient Mesopotamia*, 320,838 chars, 77,656 tokens evaluated), the model successfully ingested **96.8% of the book** in 10.1s. However, the probe reported a boundary mismatch (`match: 10%` and `match: 9%`) because rigid sentence regexes disagree with LLMs on front/back matter:
* **Start of Book:** The PDF opens with a title and copyright page (`© Copyright 2022. All Rights Reserved.`). The C# regex grabbed the first 200 characters of the title, while the LLM quoted the first grammatical sentence: `All Rights Reserved.` (10% match).
* **End of Book:** The bibliography ends with an academic citation followed by a URL/watermark (`...In Journal of Near Eastern Studies, 61, no. 2 (2002): 111–15. http://... OceanofPDF.com`). The C# regex split at the period and expected the trailing URL, while the LLM quoted the actual citation sentence (9% match).

#### The Fix
Do not rely on fragile exact single-sentence regex boundaries. Instead, verify window containment:
* Check whether the AI's recalled first quote exists within the **first 2,000 characters** of the document (`flatRange[..Math.Min(2000, flatRange.Length)]`).
* Check whether the AI's recalled last quote exists within the **last 2,000 characters** of the document (`flatRange[^Math.Min(2000, flatRange.Length)..]`).
* If normalized `recalledFirst` is found in (or has >= 80% fuzzy match with) the opening window, and `recalledLast` is found in the closing window, award **100% full match** (`PASS — Full Context Verified`).

---

### 2. Match Toolbar Icon Style and Height for "AI Test" (`MainWindow.xaml`, `Shell/SettingsPanel.cs`)

#### How This Was Previously Fixed (Reminding You of the Engine Pattern)
The app has a **caption engine** that builds every toolbar button's face (the default is large icons with text underneath). Custom hardcoded XAML stacks fail to match. The Search button was cloned for the other AI tools:
> *"Copied the Search button three times and changed only the name and the click:  
> Search → Tools (tile-grid glyph), Chat (comment-bubble glyph), Summary (document glyph).  
> Zero custom sizes, paddings, or stacks left — they are string-glyph buttons on the exact same style, so the caption engine now dresses them identically to Search in every mode: text under the icon (default), beside, text-only, icons-only, plus the same caption-shedding when the window narrows."*

#### Requirements
* Ensure `AiTestBtn` in `MainWindow.xaml` uses the exact same `Style="{StaticResource ToolbarButton}"` and glyph structure as `SearchBtn`, `SummarizeBtn`, and `AiChatBtn`.
* In `Shell/SettingsPanel.cs`, register the glyph `\uE9D9` to `"Str_Lbl_AiTest"` in `_toolbarButtons` and caption dictionary so the caption engine handles its size, height, text label, and layout identically to the other buttons in every mode.

---

### 3. Align "X" Close Button on `AiTestWindow` (`Features/AI/AiTestWindow.xaml`)

* Move the "X" close button in `AiTestWindow` to the left to match the exact margin, position, and alignment of the close chip in `SummaryWindow`.

---

### 4. Custom Page Range Input with iOS-Style Toggle in `AiTestWindow`

Add custom range testing to `AiTestWindow`:
* **iOS-Style Toggle Switch:** Add a smooth iOS-style sliding toggle switch placed immediately to the **left** of where the `20p` chip sits (and to the left of the range inputs).
* **Two Modes:**
  1. **Span Mode (Default):** Displays the preset chips (`20p`, `60p`, `100p`, `All Pages`).
  2. **Range Mode:** When the toggle is switched, hides the preset chips and displays a **"Start Range"** section with two numeric inputs:
     * **Start Page** (defaults to 1)
     * **End Page** (defaults to document page count or span)
* When in Range mode, clicking "Run Test" extracts and verifies the exact page span specified by the start and end inputs.

---

### 5. Fix "Pages Left" Counter in `SummaryWindow` (`Features/Summary/SummaryWindow.xaml.cs`)

* On `SummaryWindow`, the **"Pages left"** indicator is currently broken and always displays `0`.
* Correct the calculation: `Math.Max(0, totalPages - currentEndPage)` so that it accurately displays how many pages remain after the current range.

---

### 6. Toggle Open/Close on Keyboard Shortcuts (`MainWindow.xaml.cs`)

* For all AI-related keyboard shortcuts (`Ctrl+Shift+A` for AI Chat, shortcuts for Summary, and shortcut for AI Test):
  * If the companion window is **already open**, pressing the shortcut must **close** it.
  * If closed, pressing the shortcut opens it.

---

### 7. Smooth Fast Scroll to First Page of Range on "Next" (`Features/Summary/SummaryWindow.xaml.cs`)

* When the user clicks **"Next" / "Next Range"** in `SummaryWindow`:
  * As the PDF view and the left thumbnail sidebar advance to the next range, the document view must scroll **smoothly and fast** so that the **first page of the new range is positioned right at the top/first visible position**.

---

### Verification
1. Run `dotnet build` with zero errors.
2. Run `dotnet test` and ensure all tests pass.
3. Push to `origin/main`.
