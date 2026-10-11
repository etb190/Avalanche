# TASK: AI Citations, Web Extraction Multi-Turn Context, Streaming Display, Text Editor AI Improvements & Status Bar Mode Isolation

Repository: `https://github.com/etb190/Avalanche`  
Target Files:
- `Features/AI/AiCitations.cs`
- `Features/AI/AiChatViewModel.cs`
- `Features/AI/WebChat.cs`
- `Features/AI/AiMarkdown.cs`
- `Features/AI/QuoteLocator.cs`
- `Features/AI/prompts.default.json`
- `Controls/TextEditorDocument.cs`
- `Controls/TextEditorControl.xaml`
- `Controls/TextEditorControl.xaml.cs`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `Shell/SettingsPanel.cs`
- `Features/Viewer/MainWindowViewerHost.cs`

---

## 1. Citation Button Click Failures (Inert Circles & Missing Highlights)

### Problem:
Some citation buttons (the numbered circle badges `(1)`, `(2)`) in AI Sidechat answers do nothing when clicked. They do not scroll to the cited passage, and they do not highlight the passage on the page. In some messages, clicking the button produces no navigation, or the source footnotes chip row is completely missing.

### Root Causes:
1. **Narrow Marker Parsing in `AiCitations.cs`:**
   In `AiCitations.cs`:
   ```csharp
   private const string Open = @"[\[\uFF3B\u3010]";
   private const string Close = @"[\]\uFF3D\u3011]";
   private static readonly Regex IdRx = new(
       @"^" + Open + @"?\s*SOURCE[\s_\-]*(?<s>" + Digits + @")\s*" + Close + @"?$" +
       @"|^" + Open + @"?\s*(?<p>" + Digits + @")\s*" + Close + @"?$",
       RegexOptions.Compiled | RegexOptions.IgnoreCase);
   ```
   - Cloud models often output source IDs and inline citations with parentheses `(1)`, `(SOURCE_1)`, or Unicode circled numbers (`①`..`⑳`, code points `\u2460`..`\u2473`), as seen in real outputs.
   - Because `Open` and `Close` only match square brackets `[` / `【` / `［`, `AiCitations.ParseSourceId` fails on `(1)` and `①`, returning `-1`.
   - In `ResolveSources` / `ResolveWebSources`, `n < 1 || n > retrieved.Count` drops all such sources.
   - When dropped, `ChatMessage.Sources` is left empty. In `AiMarkdown.MakeCitation`, `FindSource` returns `null`, so the button is created disabled (`IsEnabled = false`) and inert, rendering a dead circle.
2. **Quote Location Mismatches in `QuoteLocator.LocateInChunk` & Web Highlighting:**
   - If the model alters punctuation, quotes, or whitespace slightly (e.g. smart quotes `“` vs ASCII quotes `"` or ellipsis `...` vs `…`), `LocateInChunk` marks the source `Unlocated`.
   - In web chat, `HighlightTextInTabAsync` fails if the quote has minor whitespace or character discrepancies, causing the jump to fail silently.

### Fix:
1. In `Features/AI/AiCitations.cs`:
   - Expand `Open` and `Close` to accept parentheses `(` / `)` and full-width parens `（` / `）`:
     ```csharp
     private const string Open = @"[\[\uFF3B\u3010\(\uFF08]";
     private const string Close = @"[\]\uFF3D\u3011\)\uFF09]";
     ```
   - In `ParseDigits` and `ParseSourceId`, add support for Unicode circled numbers (`①`..`⑳`, `\u2460`..`\u2473` $\rightarrow$ 1..20).
   - In `InlineRx`, match `\((?<p>\d+)\)` and `(?<c>[\u2460-\u2473])` so circled and parenthesized citation markers in text are recognized as valid footnote badges.
2. In `Features/AI/QuoteLocator.cs` and `WebBrowserControl.xaml.cs`:
   - Normalize quotes (smart quotes to ASCII, multiple spaces to single space, trim surrounding punctuation) before attempting substring matches.
   - If exact match fails, use normalized word-sequence matching (matching the first 5–8 non-stop words of the quote) so the passage is reliably located and highlighted.

---

## 2. Webpage Extraction & Multi-Turn Follow-Up Query Failure

### Problem:
In Browser Sidechat, asking a question frequently produces contradictory results: the AI answers correctly on the first question, but on follow-up questions it says:
*"The provided page text does not contain the answer to [question]."*
Asking the exact same question repeatedly toggles between answering and claiming the answer is not in the text.

### Root Cause:
In `Features/AI/AiChatViewModel.cs` (lines 1368–1378):
```csharp
bool firstWebTurn = !recentHistory.Any(
    m => m.MessageRole == ChatMessage.Role.Assistant);
string webSystem = firstWebTurn
    ? WebChat.BuildSystemPrompt(page)
    : WebChat.InstructionHead() + "\n" + WebChat.CitationContract();
```
- A previous optimization attempted to save tokens on follow-up turns by omitting the page text on turn 2+.
- Cloud LLM endpoints (OpenAI, Anthropic, Gemini, Nemotron) are **100% stateless**. They retain no memory of past system prompts across independent HTTP requests.
- On every follow-up question (`firstWebTurn == false`), `webSystem` sent ONLY the instruction head and citation contract, with **zero page text**.
- The model was commanded: *"Answer ONLY using the provided page text"*, but no page text existed in the request!
- Consequently, on every follow-up turn, the model legitimately saw no evidence and replied: *"The provided page text does not contain the answer..."*

### Fix:
In `Features/AI/AiChatViewModel.cs`:
- Remove `firstWebTurn` branching. Always include the page text in `webSystem` via `WebChat.BuildSystemPrompt(page)` on every turn.
- The model must always have the extracted page text in context so follow-up questions can reference the document accurately.

---

## 3. Streaming Displays Raw JSON Syntax & Missing Footnotes

### Problem:
While an AI Sidechat answer is streaming, the message bubble literally displays raw JSON syntax:
```json
{
  "answer": "Virgil Ivan \"Gus\" Grissom was an American astronaut...",
  "sources": [
    {
      "sourceId": "①",
      "quote": "..."
    }
```
Only when the stream completely finishes does the bubble clean up into formatted text. Furthermore, after streaming completes, source footnotes are sometimes omitted completely.

### Root Cause:
In `Features/AI/AiChatViewModel.cs` (`StreamReplyIntoAsync`):
```csharp
raw.Append(delta);
visible = StripThinkSpans(raw.ToString());
assistantMsg.Content = visible;
```
- Because the sidechat uses structured JSON output (`{"answer": "...", "sources": [...]}`), the raw SSE deltas carry JSON tokens.
- `StreamReplyIntoAsync` writes the raw accumulator `raw.ToString()` directly into `assistantMsg.Content`.
- As a result, the user watches raw JSON braces, keys, and escaped strings stream across the screen until `ParseStructuredContent` runs after completion.
- When the model returns `"sourceId": "①"`, `ParseSourceId` failed to parse `①`, dropping all sources and leaving the finished message with no source footnotes.

### Fix:
1. In `Features/AI/AiChatViewModel.cs` (`StreamReplyIntoAsync`):
   - Implement an intelligent live streaming extractor:
     ```csharp
     private static string ExtractStreamingAnswer(string raw)
     {
         if (string.IsNullOrWhiteSpace(raw)) return "";
         string clean = StripThinkSpans(raw).TrimStart();
         if (!clean.StartsWith("{", StringComparison.Ordinal))
             return clean; // not JSON, stream directly

         int keyIdx = clean.IndexOf("\"answer\"", StringComparison.OrdinalIgnoreCase);
         if (keyIdx < 0) return ""; // still waiting for answer field

         int colonIdx = clean.IndexOf(':', keyIdx + 8);
         if (colonIdx < 0) return "";

         int quoteStart = clean.IndexOf('"', colonIdx + 1);
         if (quoteStart < 0) return "";

         // Find end of string, handling escape sequences
         var sb = new System.Text.StringBuilder();
         bool escape = false;
         for (int i = quoteStart + 1; i < clean.Length; i++)
         {
             char c = clean[i];
             if (escape)
             {
                 if (c == 'n') sb.Append('\n');
                 else if (c == 'r') sb.Append('\r');
                 else if (c == 't') sb.Append('\t');
                 else sb.Append(c);
                 escape = false;
             }
             else if (c == '\\')
             {
                 escape = true;
             }
             else if (c == '"')
             {
                 // End of "answer" string - do not show trailing "sources" JSON
                 break;
             }
             else
             {
                 sb.Append(c);
             }
         }
         return sb.ToString();
     }
     ```
   - In the typewriter timer, set `visible = ExtractStreamingAnswer(raw.ToString())`.
   - The user will see clean, unescaped markdown prose stream smoothly in real time, with all JSON scaffolding completely hidden.
2. In `AiCitations.cs`:
   - Support circled numbers `①`..`⑳` and `(n)` so `sources` are never dropped.

---

## 4. Text Editor: Auto-Expanding Custom AI Input & Submit UX

### Problem:
In the Text Editor floating AI bubble, the custom instruction input is a single-line input (`<input type="text">`). When typing longer prompts or detailed rewrite instructions, the text overflows horizontally and cannot be seen. When pressing Enter, the popup should close and the input should clear for next time.

### Fix:
In `Controls/TextEditorDocument.cs`:
1. Change `ai_instr` from `<input type="text">` to an auto-expanding `<textarea>`:
   ```javascript
   var instr = document.createElement('textarea');
   instr.id = 'ai_instr';
   instr.rows = 1;
   instr.placeholder = 'Tell the AI what to do\u2026';
   instr.addEventListener('input', function() {
     this.style.height = 'auto';
     this.style.height = Math.min(this.scrollHeight, 140) + 'px';
     positionAiBubble();
   });
   ```
2. Handle Keyboard Submission:
   ```javascript
   instr.addEventListener('keydown', function(e) {
     e.stopPropagation();
     if (e.key === 'Enter' && !e.shiftKey) {
       e.preventDefault();
       var val = this.value;
       this.value = '';
       this.style.height = 'auto';
       hideAiBubble();
       requestCustomWith(val);
     }
   });
   ```
3. Update the send button (`ai_go_btn`) to also clear the textarea, reset its height, and close the bubble on click.
4. CSS styling in `TextEditorDocument.cs`:
   ```css
   #ai_instr {
     width: 220px;
     min-height: 28px;
     max-height: 140px;
     resize: none;
     overflow-y: auto;
     box-sizing: border-box;
     line-height: 1.4;
     padding: 5px 8px;
     border-radius: 4px;
   }
   ```

---

## 5. Text Editor: Replace Inappropriate Table Ribbon Icon

### Problem:
In `Controls/TextEditorControl.xaml`, `TableBtn` currently uses glyph `&#xE8EC;` (CalendarDay), which renders as a calendar rather than a table.

### Fix:
In `Controls/TextEditorControl.xaml`:
Replace the button glyph with a proper table grid vector or Segoe MDL2 Table glyph `&#xE80A;` / `&#xF0E2;`:
```xml
<Button x:Name="TableBtn" Content="&#xE80A;" Style="{StaticResource EditorBtn}"
        Click="TableBtn_Click" ToolTip="{DynamicResource Str_TT_EditorTable}"/>
```
Alternatively, style it with a crisp 2×2 grid icon using Borders matching Avalanche's standard vector toolbar icons:
```xml
<Button x:Name="TableBtn" Style="{StaticResource EditorBtn}" Click="TableBtn_Click" ToolTip="{DynamicResource Str_TT_EditorTable}">
    <Grid Width="16" Height="14">
        <Border BorderBrush="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}" BorderThickness="1" CornerRadius="1"/>
        <Rectangle Height="1" VerticalAlignment="Center" Fill="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"/>
        <Rectangle Width="1" HorizontalAlignment="Center" Fill="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"/>
    </Grid>
</Button>
```

---

## 6. Footer Status Bar: Pane Isolation & Proper Document Metadata

### Problem:
1. In Text Editor mode, the bottom status bar displays *"Cannot find that passage..."* (a lingering citation error from the PDF viewer).
2. Clicking the status text flashes the filename of the PDF from the PDF viewer instead of the text document being edited.
3. In both Text Editor and Web Browser modes, the PDF footer controls (`FooterDocumentControls`: A4 page size button `PageSizeLabel`, view mode radio buttons, and zoom dropdown `ZoomBox`) remain visible in the bottom-right corner.

### Fix:
1. **Hide PDF Document Controls When Not in PDF Mode:**
   In `Shell/SettingsPanel.cs`:
   - In `ApplyEditorToolbarFace(bool leads)`:
     When `leads == true`, set `FooterDocumentControls.Visibility = Visibility.Collapsed;`.
     When `leads == false`, restore `FooterDocumentControls.Visibility = Visibility.Visible;`.
   - In `ApplyBrowserToolbarFace(bool leads)`:
     When `leads == true`, set `FooterDocumentControls.Visibility = Visibility.Collapsed;`.
     When `leads == false`, restore `FooterDocumentControls.Visibility = Visibility.Visible;`.
2. **Context-Aware Status Text & Click Action:**
   In `MainWindow.xaml.cs` (`StatusText_Click` and `ShowCurrentFileSize`):
   - If in Text Editor mode:
     - Query `EditorControl`: display the active editor document name and word count (e.g. `document.docx — 1,240 words`).
   - If in Web Browser mode:
     - Query `WebPane`: display the active webpage title or domain.
   - If in PDF mode:
     - Keep existing PDF file size and page stats.
3. **Clear Citation Errors on Mode Switch:**
   - When switching between PDF, Browser, and Text Editor modes, clear any lingering `StatusText` error message (such as `Str_AiChatSourceNavigateFailed`) and reset to `Str_Ready`.

---

## 7. Text Editor: Render Markdown Formatting from Custom AI Commands

### Problem:
When asking the AI in Text Editor to format or generate text (e.g. *"make this section clear and bold key terms"*), the AI outputs markdown formatting such as `**example**`, `*italic*`, `### Heading`, or `- item`. The editor inserts these raw symbols verbatim into the document instead of applying rich formatting.

### Root Cause:
In `Controls/TextEditorDocument.cs` (`finishAiStream` and `applyAiResult`):
Text is inserted via `quill.updateContents(new Delta().retain(...).insert(text))` or `quill.insertText(...)`.
Quill treats `text` as a raw string literal, inserting the raw asterisks and hashtags without converting them to Delta attributes.

### Fix:
In `Controls/TextEditorDocument.cs`:
1. Add a lightweight markdown-to-HTML converter before inserting AI result text:
   ```javascript
   function mdToHtml(md) {
     if (!md) return '';
     var html = md
       .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
       .replace(/^### (.*$)/gim, '<h3>$1</h3>')
       .replace(/^## (.*$)/gim, '<h2>$1</h2>')
       .replace(/^# (.*$)/gim, '<h1>$1</h1>')
       .replace(/\*\*(.*?)\*\*/gim, '<strong>$1</strong>')
       .replace(/\*(.*?)\*/gim, '<em>$1</em>')
       .replace(/`([^`]+)`/gim, '<code>$1</code>')
       .replace(/^\s*[-*]\s+(.*$)/gim, '<ul><li>$1</li></ul>')
       .replace(/\n\n/gim, '<p></p>');
     html = html.replace(/<\/ul>\s*<ul>/gim, '');
     return html;
   }
   ```
2. When inserting custom AI text:
   If `text` contains markdown markers (`**`, `*`, `###`, `- `):
   Convert via `mdToHtml(text)` and insert using Quill's clipboard API:
   ```javascript
   var delta = quill.clipboard.convert({ html: mdToHtml(text) });
   quill.updateContents(new Delta().retain(startIdx).delete(delLen).concat(delta), 'user');
   ```
   This ensures `**bold**` renders as real bold text, headings become headings, and bullet points become formatted lists.

---

## Verification Checklist:
1. **Citation Badges:** Click citation buttons in Sidechat answers across PDF and Web modes. Verify every badge scrolls to the source and highlights the text without failing.
2. **Web Multi-Turn Chat:** Open a webpage in Browser. Ask an initial question, then ask a follow-up question ("where was he born?"). Verify the AI answers accurately using the webpage text without claiming the answer is missing.
3. **Streaming Appearance:** Ask a question in Sidechat. Verify the text streams cleanly as markdown prose without raw JSON braces (`{"answer": ...}`) visible at any point.
4. **Text Editor AI Input:** Open the floating AI bubble. Type multiple lines into the prompt. Verify the textarea expands in height. Press Enter: verify the popup closes and the textarea clears.
5. **Table Icon:** Check the Text Editor ribbon. Verify `TableBtn` displays a clear table grid icon rather than a calendar.
6. **Footer Bar Isolation:** Switch to Text Editor and Browser. Confirm the A4 size badge, view mode radios, and zoom combo disappear from the footer bar. Verify clicking the status bar shows the editor/web document info.
7. **Rich Text AI Insertion:** Instruct the AI to bold terms. Confirm `**text**` appears formatted as bold in the editor rather than showing raw asterisks.
