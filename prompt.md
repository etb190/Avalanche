# TASK: AI Subsystem Overhaul, Word Count Fix, Streaming, Interactive Tables & UI Refinements

Repository: `https://github.com/etb190/Avalanche`  
Target Files:
- `Features/Summary/PageSummarizer.cs`
- `Features/Summary/SummaryWindow.xaml.cs`
- `Features/Summary/WebSummaryWindow.xaml.cs`
- `Features/Summary/PromptStore.cs`
- `Features/AI/AiMarkdown.cs`
- `Features/AI/AiPromptLibrary.cs`
- `Features/AI/prompts.default.json`
- `Features/AI/OpenAiCompatibleProvider.cs`
- `Features/AI/AiChatViewModel.cs`
- `Features/AI/HybridRetriever.cs`
- `Features/AI/OllamaEmbeddingClient.cs`
- `Features/AI/WebChat.cs`
- `Controls/TextEditorControl.xaml`
- `Controls/TextEditorControl.xaml.cs`
- `Controls/TextEditorDocument.cs`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `Shell/SettingsPanel.cs`

---

## 1. Summary Word Count Explosion & Hidden C# Heading Overhaul

### Problem:
When a user selects a 500-word limit in the PDF Summarizer, cloud models (Nemotron, GLM, DeepSeek) generate 4,000–5,000 words. 
1. **Hidden C# Injection (`PageSummarizer.cs`):** In `DigestSystemPrompt()`, C# invisibly appends a hardcoded `BOOK HEADINGS` block that commands the model:
   *"Copy each one VERBATIM as a markdown '### ' heading and summarize the text that follows it under that heading, in flowing prose paragraphs."*
   In a 30–60 page PDF excerpt, `MarkdownNormalizer` extracts 15–25 headings from font sizes. The model is forced to generate a `### ` heading and multiple paragraphs for every single heading in the book, mathematically exploding into 4,000 words.
2. **Invisible Wrapper vs. Workshop:** The AI Prompts Workshop in Settings only exposes `prompts.json` (`GenreMandate`), while C# secretly wraps it in 300 words of rigid formatting laws, preventing the user from controlling prompt behavior.

### Requirements & Fix:
1. **Remove Hidden C# Wrappers in `PageSummarizer.cs`:**
   - Remove the hardcoded `BOOK HEADINGS` requirement from `DigestSystemPrompt()`.
   - Remove the invisible trailing rules that force verbatim section reproduction.
   - Let the prompt body from `PromptStore` / `prompts.json` be the true system prompt, substituting only standard placeholders:
     - `{words}`: Target word count from dropdown (e.g., `500`).
     - `{language}`: Output language from dropdown (e.g., `English`).
2. **Keep the Beautiful Green Headings Without Exploding:**
   - In Avalanche, `AiMarkdown.cs` renders any markdown heading (`### ` or `## `) with `PrimaryBrush` (the green accent theme color).
   - In the prompt body (e.g., `nonfiction_classic`), explicitly instruct the model to organize the summary into a small, structured set of sections:
     > *"Structure the summary into 2 to 4 overarching thematic sections using markdown `### ` headings. Under each heading, write concise flowing prose paragraphs. Connect ideas logically and ensure the total length across all sections strictly stays around approximately {words} words (do NOT exceed {words} words)."*
   - Also include a short scaffolding note in the prompt:
     > *"The text carries internal `[p. N]` page markers for reference; do not include these markers in your summary."*
3. **CRITICAL: Preserve User-Created & User-Edited Prompts:**
   - When loading or updating `PromptStore`, do **NOT** overwrite, wipe, or reset any custom prompts that users have added or edited in `%LocalAppData%\Avalanche\AI\prompts.json`.
   - Only supply default bodies for built-in prompt IDs when the user's file does not contain them.

---

## 2. Fix Global Semaphore Serializing All AI Across the App

### Problem:
In `Features/AI/OpenAiCompatibleProvider.cs`:
```csharp
private static readonly SemaphoreSlim SharedChatSemaphore = new(1, 1);
```
Every single AI call in Avalanche (Sidechat questions, PDF summarization, Notes generation, Grammar proofreading, Rewrite bubble, Connection Test) waits on this single global lock. If a large PDF summary is running, typing in AI Sidechat freezes and deadlocks until the summary completes.

### Fix:
1. Remove `SharedChatSemaphore = new(1, 1)` as a process-wide blocker.
2. Implement surface-scoped concurrency or a multi-slot throttler (e.g. allowing Sidechat and Quick Actions to execute in parallel with a background Page Summarizer).
3. If an individual endpoint host requires rate-limiting (e.g. local Ollama), scope the gate per-host (`BaseUrl`), not globally across all surfaces and cloud providers.

---

## 3. Enable Live Streaming in AI Sidechat

### Problem:
`PageSummarizer` and the Text Editor rewriter already use Server-Sent Events (SSE) streaming (`stream: true`). However, `AiChatViewModel.cs` calls `GetChatCompletionAsync` (non-streaming). When asking questions in the Sidechat, the user stares at a static "Thinking..." placeholder for 20–60 seconds before the entire markdown payload drops at once.

### Fix:
1. In `Features/AI/AiChatViewModel.cs` (`SendMessageAsync`):
   - Replace the one-shot `GetChatCompletionAsync` call with `OpenAiCompatibleProvider.GetChatCompletionStreamAsync`.
2. As chunks/deltas arrive via SSE:
   - Append to the active assistant message and update the UI in real-time.
   - Maintain full citation parsing and thought trace handling (`<think>` blocks).

---

## 4. Fix Semantic Vector Search for Cloud Users

### Problem:
In `HybridRetriever.cs` and `OllamaEmbeddingClient.cs`, vector similarity search only functions if local Ollama is running on localhost with `embeddinggemma:latest` installed. Users configured with cloud models (OpenAI, OpenRouter, Google Gemini) have vector search permanently disabled and silently degrade to BM25 keyword matching.

### Fix:
1. Generalize the embedding client in `Features/AI/`:
   - Support standard OpenAI-compatible `/v1/embeddings` endpoints (e.g. `text-embedding-3-small`, OpenRouter embeddings).
   - Support Google Gemini embedding endpoints when Google identity is active.
2. In `HybridRetriever.cs`:
   - Route embedding requests through the active provider's embedding configuration so cloud users get hybrid semantic + lexical retrieval.

---

## 5. Prevent Quadratic Document Re-Transmission on Every Chat Turn

### Problem:
In `WebChat.cs` and `AiChatViewModel.cs`, every follow-up message in a conversation re-attaches the full document or webpage extraction (up to 160,000 characters) into the conversation history. On multi-turn chats, token consumption explodes quadratically, context limits are quickly exceeded, and latency spikes.

### Fix:
1. Anchor document and webpage source extracts only once in the initial system/context turn.
2. For subsequent conversational turns, send only the rolling chat history and new retrieved RAG chunks, rather than re-prepending the entire raw document text on every turn.

---

## 6. Prevent Small Talk Regex from Intercepting Legitimate Queries

### Problem:
In `AiChatViewModel.cs`, `IsSmallTalk` uses client-side regex to intercept greetings (`hi`, `hello`, `hey`) and immediately returns a hardcoded canned greeting. If a user asks a real question that begins with a polite greeting (e.g. *"Hi, what is the conclusion on page 12?"*), the regex fires, completely ignores the document, and returns a canned "Hello! How can I help you?".

### Fix:
1. Update `IsSmallTalk` to strictly match standalone greetings (e.g. the message contains *only* a greeting with no trailing query words or punctuation).
2. If the message length exceeds ~20 characters or contains substantive question words (`what`, `why`, `how`, `page`, `summarize`, etc.), do NOT treat it as small talk; send it to the model with document context.

---

## 7. Fix Scrolling Locked to Top During Live Streaming

### Problem:
In `SummaryWindow.xaml.cs` and `WebSummaryWindow.xaml.cs`, while a summary is streaming, users cannot scroll down to read along. Any attempt to scroll down is violently jerked back up to the top (position 0).

### Root Cause:
`_typeTimer` ticks every 20ms to paint newly arrived text:
```csharp
DocBox.SetValue(AiMarkdown.TextProperty, _fullText[.._shownLength]);
```
This triggers `AiMarkdown.OnTextChanged` $\rightarrow$ `AiMarkdown.Render`:
```csharp
var doc = BuildDocument(text, parse, rtb, GetParagraphAlignment(rtb));
rtb.Document = doc; // Replaces FlowDocument!
```
In WPF, assigning a brand-new `FlowDocument` to a `RichTextBox` (`rtb.Document = doc`) destroys the visual tree and resets `VerticalOffset` to 0 (the top). Because this happens 50 times per second, the scroll position is forcibly reset to the top continuously during streaming.

### Fix:
1. In `AiMarkdown.cs` / `SummaryWindow.xaml.cs` / `WebSummaryWindow.xaml.cs`:
   - Before setting `rtb.Document = doc`, record `double currentOffset = rtb.VerticalOffset;`.
   - If the user has scrolled down (`currentOffset > 0`), restore the scroll position immediately after layout updates via `rtb.ScrollToVerticalOffset(currentOffset)`.
   - Alternatively, only auto-scroll to the bottom if the user is already at the bottom; if the user has manually scrolled up or down to read, do not jump their scroll position.

---

## 8. Ribbon Toolbar: Move Browser Summary Button to the Left Side

### Problem:
In the browser ribbon toolbar, `WebSumBtn` currently sits on the right side of the pane buttons (`PdfEditorBtn`). In the PDF editor ribbon, the Summarize button sits on the left side of the action tools. The layout is inconsistent.

### Fix:
1. In `MainWindow.xaml`:
   - Move `WebSumBtn` to the left side of the browser action tools (to the left of `TextEditorBtn` and `PdfEditorBtn`), matching the placement of `SummarizeBtn` in the PDF editor ribbon.
2. In `Shell/SettingsPanel.cs` (`ApplyBrowserToolbarFace`):
   - Ensure the toolbar reflow and visibility toggles respect the updated position on the left.

---

## 9. Fully Functional, Interactive Tables in Text Editor

### Problem:
The Text Editor lacks table creation and editing tools. Users cannot insert tables, edit rows or columns, or navigate table cells smoothly, forcing reliance on external editors.

### Technical Foundation:
Avalanche uses Quill.js v2 bundled offline in `Resources/Scripts/quill.min.js`. The bundled library already includes the full native Quill 2 Table API (`insertTable`, `insertRowAbove`, `insertRowBelow`, `insertColumnLeft`, `insertColumnRight`, `deleteRow`, `deleteColumn`, `deleteTable`).

### Implementation Details:
1. **Enable Quill Table Module in `Controls/TextEditorDocument.cs`:**
   ```javascript
   var quill = new Quill('#editor', {
     theme: 'snow',
     placeholder: '',
     modules: {
       table: true,
       toolbar: false,
       history: { delay: 400, maxStack: 500, userOnly: true }
     }
   });
   ```
2. **Table Styling in `Controls/TextEditorDocument.cs`:**
   Add clean, professional CSS styling for tables:
   ```css
   .ql-editor table {
     border-collapse: collapse;
     width: 100%;
     margin: 16px 0;
     table-layout: auto;
   }
   .ql-editor td, .ql-editor th {
     border: 1px solid #d0d7de;
     padding: 8px 12px;
     min-width: 48px;
     vertical-align: top;
     box-sizing: border-box;
   }
   /* Inverse / Dark Mode Support */
   body.az-inv .ql-editor td, body.az-inv .ql-editor th {
     border-color: #444c56;
   }
   /* Cell selection & focus outline */
   .ql-editor td:focus, .ql-editor th:focus {
     outline: 1.5px solid #4a90d9;
     outline-offset: -1px;
   }
   ```
3. **Toolbar Button in `Controls/TextEditorControl.xaml`:**
   - Add a `TableBtn` on the editor ribbon beside Image / Footnote:
     ```xml
     <Button x:Name="TableBtn" Content="&#xE8EC;" Style="{StaticResource EditorBtn}"
             Click="TableBtn_Click" ToolTip="{DynamicResource Str_TT_EditorTable}"/>
     ```
   - In `Controls/TextEditorControl.xaml.cs`:
     - Clicking `TableBtn` opens a compact table insertion popup (or inserts a default 3×3 grid) via:
       `Post(new { cmd = "insertTable", rows = 3, cols = 3 });`
4. **Interactive Table Operations (Contextual Actions):**
   - In `TextEditorDocument.cs`, track selection changes (`editor-change` / `selection-change`).
   - If the caret or selection is inside a `td` or `th`:
     - Report `inTable: true` to WPF in `reportState()`.
   - Implement handlers in `TextEditorDocument.cs` for table commands:
     - `table.insertTable(rows, cols)`
     - `table.insertRowAbove()`
     - `table.insertRowBelow()`
     - `table.insertColumnLeft()`
     - `table.insertColumnRight()`
     - `table.deleteRow()`
     - `table.deleteColumn()`
     - `table.deleteTable()`
   - Provide intuitive UI access: either contextual toolbar buttons enabled when `inTable` is true, or a sleek floating bubble/context-menu offering row/column insertion and deletion.
5. **Keyboard Navigation & Behavior:**
   - Handle `Tab` inside a cell to advance to the next cell.
   - If `Tab` is pressed in the last cell of the table, automatically insert a new row below and move caret into the first cell of the new row.
   - Handle `Shift+Tab` to move to the previous cell.
6. **Undo/Redo & Persistence:**
   - Ensure all table insertions and edits register with Quill's `history` module for seamless `Ctrl+Z` / `Ctrl+Y` undo/redo.
   - Ensure the HTML serializer (`dump` / `save`) serializes `<table>` elements cleanly and restores them on load.

---

## 10. Fix AI Sidechat Jerky / Buggy Scrolling

### Problem:
Scrolling the message history in the AI Sidechat is glitchy, rubber-banding, and jerking back and forth when the mouse cursor is over chat message bubbles. Scrolling over the bottom input prompt area or empty margins is completely smooth.

### Root Cause:
In `MainWindow.xaml`:
`AiChatScrollViewer` wraps `AiChatMessages` (`ItemsControl`), which renders each message bubble using a nested `RichTextBox` (`MessageRichText`).
In WPF, `RichTextBox` inherits from `TextBoxBase`. Even though `VerticalScrollBarVisibility="Disabled"` is set, WPF's `RichTextBox` intercepts and handles `MouseWheel` events internally instead of cleanly bubbling them up to `AiChatScrollViewer`. The nested text box partially consumes scroll deltas and fights the parent container, producing severe stutter and jerking.

### Fix:
In `Features/AI/AiMarkdown.cs` (inside `EnsureHandlers(RichTextBox rtb)`):
Attach a `PreviewMouseWheel` event handler to tunnel mouse wheel deltas directly to the parent `ScrollViewer`:
```csharp
private static void OnRichTextBoxPreviewMouseWheel(object sender, MouseWheelEventArgs e)
{
    if (sender is RichTextBox rtb && !e.Handled)
    {
        e.Handled = true;
        var parentScrollViewer = FindVisualParent<ScrollViewer>(rtb);
        if (parentScrollViewer != null)
        {
            parentScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            });
        }
    }
}
```
Helper:
```csharp
private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
{
    var parent = VisualTreeHelper.GetParent(child);
    while (parent != null && parent is not T)
    {
        parent = VisualTreeHelper.GetParent(parent);
    }
    return parent as T;
}
```
This guarantees that scrolling anywhere inside the chat area—including directly over long markdown bubbles—scrolls the outer `AiChatScrollViewer` smoothly without hesitation or rubber-banding.
