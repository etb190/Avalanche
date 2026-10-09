# TASK: Add Grammar/Rewrite Model Dials & Port Axo Journal Editor Engine

Repository: `https://github.com/etb190/Avalanche`  
Reference Source: `c:\Users\PC\Desktop\Coding\Axo\src\components\DailyJournal.jsx`  
Target Files:
- `Features/AI/AiSurfaceModels.cs`
- `Features/AI/AiSettingsViewModel.cs`
- `Shell/SettingsPanel.cs`
- `Controls/TextEditorControl.xaml.cs`
- `Controls/TextEditorControl.xaml`
- `Controls/TextEditorDocument.cs`
- `Strings/*.xaml`

---

## PART 1: Grammar Model & Rewrite Model in AI Settings

In Avalanche's AI settings, replace the single `TextEditor` dial with **two independent model selection dials**:
1. **Grammar Model** (`AiSurface.EditorGrammar`): Controls which AI model executes the automatic timer-based proofreading and grammar fixes.
2. **Rewrite Model** (`AiSurface.EditorRewrite`): Controls which AI model executes the 7-voice rewriter (Humanize, Professional, Simple, Academic, Jargon, Lengthen, Shorten).

### Implementation Steps:
1. **`Features/AI/AiSurfaceModels.cs`**:
   - Update `AiSurface` enum:
     ```csharp
     internal enum AiSurface { Summary, Sidechat, WebSidechat, Recaller, AiTester, Notes, EditorGrammar, EditorRewrite }
     ```
   - In `SurfaceChoices`, add `EditorGrammar` and `EditorRewrite` (defaulting to `NemotronChoice` or `GeminiChoice`).
   - Add getters/setters in `AiSurfaceModels`.
2. **`Features/AI/AiSettingsViewModel.cs`**:
   - Expose properties `EditorGrammarModelChoice` and `EditorRewriteModelChoice`.
   - Wire `OnPropertyChanged()` and call `AiSurfaceModels.Set(...)`.
3. **`Shell/SettingsPanel.cs`**:
   - Add two combo boxes in the AI settings panel for **Grammar Model** and **Rewrite Model**, populated with the same standard choices (Nemotron, Gemini Flash, DeepSeek, Kimi, GLM, Ollama).
4. **`Controls/TextEditorControl.xaml.cs`**:
   - Update `RunAiGrammarScanAsync`:
     ```csharp
     var config = Features.AI.AiSurfaceModels.Configure(_aiSettings.ToGenConfig(), Features.AI.AiSurface.EditorGrammar);
     ```
   - Update `RunAiSelectionAsync` (rewriter):
     ```csharp
     var config = Features.AI.AiSurfaceModels.Configure(_aiSettings.ToGenConfig(), Features.AI.AiSurface.EditorRewrite);
     ```

---

## PART 2: Port Axo Journal Editor Architecture & Behaviors

Replicate the exact rich text editor from **Axo** (`c:\Users\PC\Desktop\Coding\Axo\src\components\DailyJournal.jsx`). The editor must have all of Axo's features, typography, shortcuts, and **especially how it behaves**.

### 1. Smart Typing Behaviors (From Axo's `handleTextInput`)

Implement these exact live input rules in the editor's JavaScript:

#### A. Autocorrect standalone 'i' to 'I'
When the user types lowercase `i` followed by a space, automatically turn it into capitalized `I `:
```javascript
// Autocorrect isolated 'i' to 'I' when followed by a space
if (text === ' ' && /(^|\s)i$/.test(textBefore)) {
    // Replace trailing 'i' with 'I '
}
```

#### B. Auto-Capitalization of First Letter of Sentences
Automatically capitalize the first letter:
1. At the very start of a block (new paragraph / new line).
2. Immediately following sentence enders (`.`, `!`, `?`) and one or more spaces:
```javascript
const isStartOfBlock = (caretOffset === 0);
const isAfterSentenceEnd = /[.!?]\s+$/.test(textBefore);

if (isStartOfBlock || isAfterSentenceEnd) {
    if (char >= 'a' && char <= 'z') {
        insertChar(char.toUpperCase());
    }
}
```

---

### 2. Line Spacing Modes (Compact, Normal, Relaxed)

Axo features a line-spacing toggle with three discrete modes:
* **Compact:** `line-height: 1.25`, `paragraph margin-bottom: 0.15em`
* **Normal:** `line-height: 1.6`, `paragraph margin-bottom: 0.5em`
* **Relaxed:** `line-height: 1.8`, `paragraph margin-bottom: 1.0em`

Expose a Spacing button on the toolbar that cycles: `Compact -> Normal -> Relaxed -> Compact`, updating CSS variables `--line-height` and `--p-margin`.

---

### 3. Complete Formatting & Shortcut Parity

Port all tools and keyboard shortcuts directly from Axo:

| Tool | Shortcut | Behavior |
|------|----------|----------|
| **Bold** | `Ctrl+B` | Toggles bold |
| **Italic** | `Ctrl+I` | Toggles italic |
| **Underline** | `Ctrl+U` | Toggles underline |
| **Strikethrough** | `Ctrl+Shift+X` | Toggles strikethrough |
| **Heading 1** | `Ctrl+Alt+1` | Large header (2em, line-height 1.2) |
| **Heading 2** | `Ctrl+Alt+2` | Medium header (1.5em, line-height 1.3) |
| **Bullet List** | `Ctrl+Shift+8` | Unordered list |
| **Ordered List** | `Ctrl+Shift+7` | Numbered list |
| **Blockquote** | `Ctrl+Shift+B` | Blockquote (with styled quotes `“...”`) |
| **Code Block** | `Ctrl+Alt+C` | VS Code-style syntax-highlighted block |
| **Inline Code** | `Ctrl+E` | Monospace inline pill |
| **Align Left** | `Ctrl+Shift+L` | Left text align |
| **Align Center** | `Ctrl+Shift+E` | Center text align |
| **Align Right** | `Ctrl+Shift+R` | Right text align |

---

### 4. Axo Typography & Styling

Adopt Axo's custom CSS rules:
1. **Tight List Packing:**
   ```css
   ul, ol {
     padding-left: 0 !important;
     margin-left: 1em !important;
     margin-top: 0 !important;
     margin-bottom: var(--p-margin) !important;
   }
   li {
     display: flex !important;
     align-items: baseline !important;
     gap: 0.4em !important;
     margin-top: 0 !important;
     margin-bottom: -0.1em !important; /* Extremely tight vertical packing */
   }
   p + ul, p + ol {
     margin-top: calc(var(--p-margin) * -0.5) !important;
   }
   ```
2. **VS Code Dark Code Blocks:**
   ```css
   pre {
     background: #1e1e1e !important;
     color: #d4d4d4 !important;
     font-family: 'JetBrains Mono', 'Fira Code', 'Consolas', monospace !important;
     padding: 0.75rem 1rem !important;
     border-radius: 0.5rem !important;
     margin: 0.5rem 0 !important;
   }
   code {
     background-color: rgba(110, 118, 129, 0.4);
     padding: 0.2em 0.4em;
     border-radius: 6px;
     font-size: 85%;
     font-family: Consolas, 'Courier New', monospace;
   }
   ```
3. **Headings:**
   - `h1`: `font-size: 2em; line-height: 1.2; margin: 0.5em 0 0.25em 0;`
   - `h2`: `font-size: 1.5em; line-height: 1.3; margin: 0.5em 0 0.25em 0;`

---

## 5. Verification Checklist

1. [ ] Settings panel shows separate dropdowns for **Grammar Model** and **Rewrite Model**.
2. [ ] Selections for both dials persist across launches in `surface-models.json`.
3. [ ] Automatic grammar check uses the configured Grammar model; rewriter uses the Rewrite model.
4. [ ] Typing lowercase `i` followed by space autocorrects to uppercase `I `.
5. [ ] Typing at the start of a paragraph or after `. ! ?` + space automatically capitalizes the first letter.
6. [ ] Line spacing button cycles through Compact, Normal, and Relaxed.
7. [ ] All shortcuts (`Ctrl+B`, `Ctrl+I`, `Ctrl+U`, `Ctrl+Shift+X`, `Ctrl+Alt+1`, `Ctrl+Alt+2`, `Ctrl+Shift+8`, `Ctrl+Shift+7`, `Ctrl+Shift+B`, `Ctrl+Alt+C`, `Ctrl+E`, `Ctrl+Shift+L`, `Ctrl+Shift+E`, `Ctrl+Shift+R`) work reliably.
8. [ ] Code blocks render with dark VS Code styling.
9. [ ] Solution builds with 0 errors and all tests pass.
