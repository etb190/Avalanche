# TASK: AI Assistant for Text Editor (Timer-Based Grammar Checker & Selection Rewriter)

Repository: `https://github.com/etb190/Avalanche`  
Target Files:
- `Controls/TextEditorDocument.cs`
- `Controls/TextEditorControl.xaml.cs`
- `Controls/TextEditorControl.xaml`
- `Features/AI/AiSurfaceModels.cs`
- `Strings/*.xaml`

---

## 1. Core Architecture & Workflow

There are **two distinct operational modes**:

### Mode 1: TIMER-BASED Automatic Grammar Check (Batch Scan — Never on Keystroke)
- **NO requests while typing.** Keystrokes NEVER send API calls.
- A **quiet idle timer** (e.g. 5 seconds after the user stops typing, or when the user pauses):
  - Runs **only if the document was modified** since the last check.
  - Sends the edited section in **ONE single batched API request** to preserve rate limits.
  - When the response arrives, **all grammar and spelling issues appear across the document at once**:
    - Problematic words are underlined in red wavy underline (`.ai-err`).
    - Clicking any red word reveals the Grammarly-style popup directly above it:
      - The **proper word replacement** (clicking it instantly swaps the word in Quill).
      - An **`[✕]` ignore button** (dismisses the flag and adds the word to an ignored set so it is never flagged again).
  - If the grammar is fine, no red underlines appear and no changes are made.

### Mode 2: SELECTION-BASED Rewrite & Deep Fix (When Text Is Selected)
- When the user selects a chunk of text (phrase, sentence, or multiple paragraphs):
  - A floating action bar (`#ai_bubble`) appears above the selection with:
    - **`[ 🪄 Fix Grammar ]`** (Deep multi-sentence grammatical & structural polish on the selection).
    - **`[ ✍️ Rewrite ▾ ]`** (Dropdown with styles: Humanize, Professional, Simple, Academic, Jargon, Lengthen, Shorten).
  - If "Fix Grammar" runs on a selection that has no errors, a subtle notification says *"Grammar looks good!"* and nothing changes.

### Extra Requirement: Blockquote Quotes
- Quotes/blockquotes in Quill (`<blockquote>`) must have styled opening and closing quotation marks encasing them (`“...”`).

---

## 2. Timer-Based Batch Grammar Checker (`TextEditorDocument.cs`)

### A. The Idle Batch Timer (Guards Rate Limits)
```javascript
var scanTimer = null;
var isDirty = false;
var lastScannedHash = '';
var ignoredWords = new Set();
var SCAN_IDLE_DELAY_MS = 5000; // 5 seconds of idle stillness after typing stops

quill.on('text-change', function(delta, oldDelta, source) {
  if (source !== 'user') return;
  isDirty = true;
  
  // Reset the idle timer on every edit so typing NEVER triggers a call
  if (scanTimer) clearTimeout(scanTimer);
  scanTimer = setTimeout(triggerTimerScan, SCAN_IDLE_DELAY_MS);
});

function triggerTimerScan() {
  if (!isDirty) return;
  isDirty = false;
  
  var fullText = quill.getText().trim();
  if (fullText.length < 5) return;
  
  var hash = hashString(fullText);
  if (hash === lastScannedHash) return;
  lastScannedHash = hash;
  
  // Single batched request for the whole document/section
  post({
    type: 'ai_timer_grammar_scan',
    text: fullText,
    ignored: Array.from(ignoredWords)
  });
}
```

### B. Prompt for Timer Grammar Scanner (`TextEditorControl.xaml.cs`)
```text
System: You are an expert copyeditor and proofreader.
Scan the provided text and identify all misspelled words, poor word choices, and grammatical mistakes.
DO NOT flag words in this ignored list: [{{ignoredWords}}].

Output ONLY valid JSON with this exact schema (no markdown, no conversational text):
{
  "errors": [
    {
      "word": "exact misspelled/poor word in text",
      "suggestion": "corrected replacement",
      "reason": "Spelling|Grammar|Word Choice"
    }
  ]
}
If there are no errors, return: {"errors": []}
```

### C. Client-Side Rendering of All Found Issues
- When the batch response arrives, all flagged words are highlighted simultaneously:
  ```css
  .ai-err {
    border-bottom: 2px wavy #e53e3e;
    background: rgba(229, 62, 62, 0.08);
    cursor: pointer;
    transition: background 0.15s ease;
  }
  .ai-err:hover {
    background: rgba(229, 62, 62, 0.18);
  }
  ```
- Clicking any `.ai-err` displays a floating card directly above the word:
  ```html
  <div id="err_pop">
    <button class="err-fix">the</button>
    <button class="err-x" title="Ignore">✕</button>
  </div>
  ```
- Clicking `.err-fix`:
  - Replaces the word in Quill using `'user'` source (preserves `Ctrl+Z`).
  - Closes popover and removes underline.
- Clicking `.err-x`:
  - Adds word to `ignoredWords`.
  - Removes the `.ai-err` styling immediately.
  - Closes popover.

---

## 3. Selection-Based Rewriter & Styles (`#ai_bubble`)

When a range is selected (`quill.getSelection().length > 3`), show `#ai_bubble` above the selection.

### Style Options:
1. **Humanize (Deep Stylometry Anti-AI Rules):**
   - High burstiness: mix short 3-word punchy sentences with natural longer sentences.
   - Absolutely BANNED cliché AI vocabulary: *"delve", "testament", "tapestry", "crucial", "pivotal", "foster", "intertwined", "multifaceted", "underscores", "moreover", "beacon", "furthermore", "in conclusion"*.
   - Break 3-part parallelisms; use natural idioms and everyday contractions (*it's, don't, can't*).
2. **Professional:** Crisp, direct, active voice, workplace-appropriate, clear and polite.
3. **Simple:** Plain English, 8th-grade reading level (Flesch-Kincaid 60+), short words, direct active sentences.
4. **Academic:** Disciplined, scholarly vocabulary and formal analytical framing.
5. **Jargon:** Complicated, dense, bureaucratic, and intentionally obtuse prose aimed at making people unable to read and understand easily (heavy nominalizations, passive voice, corporate/academic buzzwords).
6. **Lengthen:** Elaborates and expands phrasing purely to make it longer **WITHOUT** adding new facts, substance, or hallucinated ideas.
7. **Shorten:** Ruthlessly condenses the text to its core meaning, eliminating all fluff.

---

## 4. Blockquotes Quotation Marks (Unrelated Fix)

In `Controls/TextEditorDocument.cs`, encase blockquotes in styled opening and closing quotes (`“...”`):
```css
.ql-editor blockquote {
  border-left: 3px solid #7aa7d8;
  padding-left: 14px;
  margin: 12px 0;
  font-style: italic;
  color: #444;
  position: relative;
}
.ql-editor blockquote::before {
  content: "“";
  font-family: Georgia, serif;
  font-size: 1.5em;
  line-height: 0.1em;
  vertical-align: -0.2em;
  margin-right: 4px;
  color: #7aa7d8;
}
.ql-editor blockquote::after {
  content: "”";
  font-family: Georgia, serif;
  font-size: 1.5em;
  line-height: 0.1em;
  vertical-align: -0.2em;
  margin-left: 4px;
  color: #7aa7d8;
}
```

---

## 5. Rate-Limit Safeguards Summary

1. **NO requests while typing:** Typing constantly resets the 5-second idle timer, so typing 500 words in a row produces **zero** API requests.
2. **Single batched scan:** Only when the user pauses for 5 seconds does a single API request fire, finding and highlighting all issues in one shot.
3. **Content hashing:** An untouched document produces **zero** requests even after minutes or hours of sitting open.
4. **Ignored words cache:** Dismissed words are saved in a local Set and never re-scanned.
5. **Selection actions (Rewrite & Fix Grammar)** are strictly on-demand on explicit button click.
