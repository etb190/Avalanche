# TASK: Comprehensive AI Citations, Web Extraction Multi-Turn Context, Streaming Display, Text Editor Improvements, Ribbon Toolbar Consistency & Status Bar Isolation

Repository: `https://github.com/etb190/Avalanche`  
Target Files:
- `Features/AI/AiCitations.cs`
- `Features/AI/AiChatViewModel.cs`
- `Features/AI/AiChatText.cs`
- `Features/AI/WebChat.cs`
- `Features/AI/AiMarkdown.cs`
- `Features/AI/QuoteLocator.cs`
- `Features/AI/prompts.default.json`
- `Controls/TextEditorDocument.cs`
- `Controls/TextEditorControl.xaml`
- `Controls/TextEditorControl.xaml.cs`
- `Controls/Viewer/PdfViewer.TabsApi.cs`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `Shell/SettingsPanel.cs`
- `Features/Viewer/MainWindowViewerHost.cs`

---

## 1. Citation Button Click Failures ("Always One Source That Fails" & Dead Circles)

### Problem:
1. In AI Sidechat responses, there is frequently one source citation badge (e.g. `(1)`, `(2)`) where clicking does absolutely nothing: it does not scroll the document, does not jump to the page, and draws no highlight.
2. In other cases, citation buttons render as disabled/inert circles with no tooltips and no action, or the footnote chip list is completely omitted.

### Root Causes:
1. **Narrow Marker Parsing in `AiCitations.cs`:**
   In `AiCitations.cs`:
   ```csharp
   private const string Open = @"[\[\uFF3B\u3010]";
   private const string Close = @"[\]\uFF3D\u3011]";
   ```
   - Cloud models (OpenAI, Anthropic, Gemini, Nemotron) frequently output parenthesized citations `(1)`, `(SOURCE_1)`, or Unicode circled numbers (`①`..`⑳`, code points `\u2460`..`\u2473`).
   - `AiCitations.ParseSourceId` fails on `(1)` and `①`, returning `-1`.
   - In `ResolveSources` / `ResolveWebSources`, `n < 1 || n > retrieved.Count` drops all such sources from `ChatMessage.Sources`.
   - In `AiMarkdown.MakeCitation`, `FindSource` returns `null`, so the button is created disabled (`IsEnabled = false`) and inert.
2. **Quote Location Failure & Missing Chunk Fallback in `MainWindow.xaml.cs`:**
   In `MainWindow.xaml.cs` (`HighlightAiSourceOnPageCore` & `DrawAiHighlightForPage`):
   ```csharp
   if (source is null || source.Location == Features.AI.AiQuoteLocation.Unlocated ||
       string.IsNullOrWhiteSpace(source.Quote))
   {
       return false;
   }
   ```
   - When a model synthesizes an answer across 2–4 citations, one quote is almost always slightly paraphrased, condensed, or omits intermediate punctuation.
   - If exact word sequence matching fails in `QuoteLocator.LocateInChunk`, `source.Location` becomes `Unlocated`.
   - `DrawAiHighlightForPage` returns `false`, logs `highlight FAILED`, and paints **NOTHING**.
   - There was zero fallback: despite having the valid retrieved `chunk` with `chunk.PageIndex` and `chunk.BoundingBox`, the code gave up entirely, leaving the user with an unresponsive click.
3. **`PageList.SelectedIndex` No-Op When Already on Page:**
   In `MainWindow.xaml.cs` (`NavigateToAiSource`):
   ```csharp
   PageList.SelectedIndex = pageIndex;
   ```
   - If the user is already viewing `pageIndex`, setting `PageList.SelectedIndex = pageIndex` does not raise `SelectionChanged`.
   - Continuous scroll view is NOT scrolled to the passage, and no navigation occurs.

### Fix:
1. In `Features/AI/AiCitations.cs`:
   - Expand `Open` and `Close` to accept parentheses `(` / `)` and full-width parens `（` / `）`:
     ```csharp
     private const string Open = @"[\[\uFF3B\u3010\(\uFF08]";
     private const string Close = @"[\]\uFF3D\u3011\)\uFF09]";
     ```
   - In `ParseDigits` and `ParseSourceId`, support Unicode circled numbers (`①`..`⑳`, `\u2460`..`\u2473` $\rightarrow$ 1..20).
   - In `InlineRx`, match `\((?<p>\d+)\)` and `(?<c>[\u2460-\u2473])` so circled and parenthesized citation markers in text are recognized as valid footnote badges.
2. In `MainWindow.xaml.cs` (`NavigateToAiSource` & `HighlightAiSourceOnPageCore`):
   - Force active viewer scroll even if already on the page:
     ```csharp
     if (PageList.SelectedIndex == pageIndex)
         viewer?.NavigateToPageAnimatedExt(pageIndex);
     else
         PageList.SelectedIndex = pageIndex;
     ```
   - Add Chunk Fallback Highlighting: When `DrawAiHighlightForPage` fails because the model paraphrased the quote (`source.Location == Unlocated`), do not abandon the user. Fall back to highlighting the chunk's page bounding box (`chunk.BoundingBox` / `chunk.PageSizes`) and scroll the viewer to that chunk location:
     ```csharp
     if (!DrawAiHighlightForPage(viewer, pageIndex, canvas, chunk, source))
     {
         if (TryDrawAiChunkFallbackHighlight(canvas, viewer, pageIndex, chunk))
             SetStatus(Loc("Str_AiChatSourceApproximate"));
         else
             SetStatus(Loc("Str_AiChatSourceNavigateFailed"));
     }
     ```
3. In `Features/AI/QuoteLocator.cs` and `WebBrowserControl.xaml.cs`:
   - Normalize quotes (smart quotes to ASCII, multiple spaces to single space, trim punctuation) before matching.
   - If exact match fails, use normalized word-sequence matching (matching the first 5–8 non-stop words of the quote) to locate the passage.

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
      "sourceId": "SOURCE_1",
      "quote": "..."
    }
  ]
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

## 4. Text Editor: Auto-Expanding AI Input & Sleek Dark Scrollbar

### Problem:
1. In the Text Editor floating AI bubble, the custom instruction input was a single-line `<input type="text">`. When typing longer prompts or detailed rewrite instructions, the text overflows horizontally and cannot be seen.
2. After changing to an expanding `<textarea>`, when the textarea reaches its max height and scrolls, Chromium/WebView2 renders a default, chunky, stark-white 16px scrollbar that ruins the dark theme.

### Fix:
In `Controls/TextEditorDocument.cs`:
1. Use an auto-expanding `<textarea id="ai_instr">` with keyboard submission (Enter submits, Shift+Enter creates a new line).
2. Inject custom WebKit scrollbar styles for `#ai_instr` so the scrollbar is thin, sleek, and translucent dark:
   ```css
   #ai_bubble textarea {
     flex: 1;
     width: 220px;
     min-width: 150px;
     min-height: 28px;
     max-height: 140px;
     resize: none;
     overflow-y: auto;
     box-sizing: border-box;
     line-height: 1.4;
     padding: 5px 8px;
     border: 1px solid #4a5160;
     border-radius: 4px;
     background: #1e2229;
     color: #e8e8e8;
     font: 12px 'Segoe UI', sans-serif;
     outline: none;
     scrollbar-width: thin;
     scrollbar-color: rgba(255, 255, 255, 0.25) transparent;
   }
   #ai_bubble textarea:focus {
     border-color: #7aa7d8;
   }
   #ai_bubble textarea::-webkit-scrollbar {
     width: 5px;
   }
   #ai_bubble textarea::-webkit-scrollbar-track {
     background: transparent;
   }
   #ai_bubble textarea::-webkit-scrollbar-thumb {
     background: rgba(255, 255, 255, 0.22);
     border-radius: 3px;
   }
   #ai_bubble textarea::-webkit-scrollbar-thumb:hover {
     background: rgba(255, 255, 255, 0.45);
   }
   ```
3. Update `ai_go_btn` and Enter keydown handler to clear the textarea, reset its height to `auto`, and close the bubble on submission.

---

## 5. Text Editor: Replace Inappropriate Table Ribbon Icon

### Problem:
In `Controls/TextEditorControl.xaml`, `TableBtn` currently uses glyph `&#xE8EC;` (CalendarDay), which renders as a calendar rather than a table.

### Fix:
In `Controls/TextEditorControl.xaml`:
Replace the button glyph with a crisp 2×2 grid icon using Borders matching Avalanche's standard vector toolbar icons:
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

---

## 8. AI Sidechat: Auto-Scroll to Bottom on Question Send & Live Streaming

### Problem:
When sending a question in AI Sidechat (across PDF, Browser, and Text Editor), the chat message container does not scroll to the bottom. The user has to manually scroll down to see the question they just asked and the pending assistant answer.

### Root Cause:
In `MainWindow.xaml.cs` (`AiChatSendBtn_Click`):
```csharp
var input = _aiChatViewModel.CurrentInput;
_aiChatViewModel.CurrentInput = "";
await _aiChatViewModel.SendMessageAsync(input);

// Scroll to bottom - fire-and-forget UI update
_ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
{
    AiChatScrollViewer?.ScrollToBottom();
});
```
- `await _aiChatViewModel.SendMessageAsync(input)` awaits the entire response streaming process (which can take 15–30 seconds)!
- `AiChatScrollViewer?.ScrollToBottom()` was only scheduled **AFTER** the entire generation finished!
- While sending and while streaming, the scroll viewer sat static or forced the user to manually scroll.

### Fix:
1. In `MainWindow.xaml.cs` (`AiChatSendBtn_Click`):
   - Dispatch `ScrollToBottom()` **immediately** before awaiting `SendMessageAsync`:
     ```csharp
     var input = _aiChatViewModel.CurrentInput;
     _aiChatViewModel.CurrentInput = "";

     // Immediately scroll to bottom so the new question and pending reply are in view:
     _aiChatAutoScroll = true;
     Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AiChatScrollViewer?.ScrollToBottom());

     await _aiChatViewModel.SendMessageAsync(input);

     Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AiChatScrollViewer?.ScrollToBottom());
     ```
2. In `MainWindow.xaml` and `MainWindow.xaml.cs`:
   - Wire `ScrollChanged="AiChatScrollViewer_ScrollChanged"` on `AiChatScrollViewer`.
   - If `e.ExtentHeightChange > 0` and auto-scroll is enabled (or the user is already near the bottom within 25px), keep pinned to bottom:
     ```csharp
     private bool _aiChatAutoScroll = true;

     private void AiChatScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
     {
         if (e.ExtentHeightChange > 0)
         {
             if (_aiChatAutoScroll || e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ExtentHeightChange - 25)
             {
                 AiChatScrollViewer?.ScrollToBottom();
             }
         }
         else if (e.VerticalChange < -5)
         {
             // User manually scrolled up: pause autoscroll so they can read previous turns
             _aiChatAutoScroll = false;
         }
     }
     ```

---

## 9. Corrupted Multi-Citations (`[ , ]`), Missing Citations & Double Spaces (` .`)

### Problem:
In some AI responses, citation markers are completely mangled into empty brackets `[ , ]` or leave double spaces before punctuation:
```
- Spaceflight Firsts: He was one of the original Mercury Seven astronauts selected by NASA in 1959  . On July 21, 1961, he became the second American to fly in space when he piloted Liberty Bell 7 (Mercury-Redstone 4) [ ,  ]. When he commanded Gemini 3 on March 23, 1965, he became the first NASA astronaut to fly into space twice  , and the second American overall after X-15 test pilot Joe Walker  .
```

### Root Cause:
1. **Flawed Marker Stripping in `AiChatText.StripCitationMarkers`:**
   In `Features/AI/AiChatViewModel.cs` (`GetRecentMessagesFrom`):
   ```csharp
   .Select(m => m.MessageRole == ChatMessage.Role.Assistant
       ? new ChatMessage { MessageRole = m.MessageRole, Content = AiChatText.StripCitationMarkers(m.Content) }
       : m)
   ```
   And in `Features/AI/AiChatText.cs`:
   ```csharp
   public static string StripCitationMarkers(string? text)
   {
       if (string.IsNullOrEmpty(text)) return "";
       return AiCitations.InlineRx.Replace(text, "").Trim();
   }
   ```
   - When a model outputs a combined citation like `[SOURCE_2, SOURCE_3]` or `[2, 3]`, `AiCitations.InlineRx` only matches `\bSOURCE_2` and `\bSOURCE_3` as bare tokens.
   - Replacing those tokens with `""` leaves the surrounding square brackets and comma intact: `[ ,  ]`!
   - Replacing `[SOURCE_1]` in `NASA in 1959 [SOURCE_1] .` leaves `NASA in 1959  .`.
   - When this stripped string is passed as history to the next turn, the LLM reads `[ ,  ]` and `  .` as the expected syntax, and faithfully reproduces the corrupted formatting in subsequent answers!
2. **Missing Multi-Citation Support in `AiCitations.cs` and `AiMarkdown.cs`:**
   `InlineRx` does not recognize comma-delimited brackets like `[1, 2]` or `[SOURCE_1, SOURCE_2]`.

### Fix:
1. In `Features/AI/AiChatText.cs` (`StripCitationMarkers`):
   - Replace combined citation brackets, remove leftover empty brackets `\[\s*(?:,\s*)*\]`, collapse whitespace before punctuation, and normalize double spaces:
     ```csharp
     public static string StripCitationMarkers(string? text)
     {
         if (string.IsNullOrEmpty(text)) return "";
         // 1. Strip all inline citation markers
         string stripped = AiCitations.InlineRx.Replace(text, "");
         // 2. Strip leftover empty or comma-only brackets like [ , ] or [,]
         stripped = Regex.Replace(stripped, @"\[\s*(?:,\s*)*\]", "");
         // 3. Collapse whitespace preceding punctuation marks (e.g. "1959  ." -> "1959.")
         stripped = Regex.Replace(stripped, @"\s+([.,;:!?])", "$1");
         // 4. Collapse multiple spaces into a single space
         stripped = Regex.Replace(stripped, @"\x20{2,}", " ");
         return stripped.Trim();
     }
     ```
2. In `Features/AI/AiCitations.cs` & `AiMarkdown.cs`:
   - Expand multi-citations before parsing: convert `\[\s*(?:SOURCE[\s_\-]*)?(\d+)\s*,\s*(?:SOURCE[\s_\-]*)?(\d+)\s*\]` into `[SOURCE_$1] [SOURCE_$2]` so each citation renders as an independent clickable badge.
3. In `Features/AI/prompts.default.json` and `WebChat.CitationContract()`:
   - Add explicit formatting instruction:
     `"If citing multiple sources for one statement, write separate markers like [SOURCE_1] [SOURCE_2]. NEVER write combined brackets like [SOURCE_1, SOURCE_2] or [ , ]."`

---

## 10. AI Chat Ribbon Button on Far Right in Browser & Text Editor Modes

### Problem:
In PDF Editor mode, the ribbon toolbar features the `AI Chat` button (`AiChatBtn`) on the far right next to `SummarizeBtn` and `SearchBtn`.  
However, when switching to Browser mode or Text Editor mode, `RightContainer` is collapsed:
- In Browser mode, only `BrowserSumBar` (`WebSumBtn`) is shown, with no AI Chat button.
- In Text Editor mode, neither Summary nor AI Chat is present on the ribbon.
The user expects the `AI Chat` button to always be accessible on the far right of the ribbon toolbar across Browser and Text Editor modes.

### Fix:
1. In `MainWindow.xaml`:
   - Move or provide the AI Chat button in a shared far-right action container (or add `WebAiChatBtn` inside `BrowserSumBar`, and an editor action group), wired to `AiChatBtn_Click`.
   ```xml
   <StackPanel x:Name="BrowserSumBar" Orientation="Horizontal"
               HorizontalAlignment="Right" Visibility="Collapsed">
       <Button x:Name="WebSumBtn" Content="&#xE8A5;" Style="{StaticResource ToolbarButton}"
               Click="WebSumBtn_Click" ToolTip="{DynamicResource Str_TT_WebSummarize}"/>
       <Button x:Name="WebAiChatBtn" Content="&#xE8BD;" Style="{StaticResource ToolbarButton}"
               Click="AiChatBtn_Click" ToolTip="{DynamicResource Str_TT_AiChat}"/>
   </StackPanel>
   ```
2. In `Shell/SettingsPanel.cs`:
   - In `ApplyBrowserToolbarFace(bool leads)`:
     When `leads == true`, ensure both `WebSumBtn` and `WebAiChatBtn` (or `AiChatBtn`) are visible in the far-right container.
   - In `ApplyEditorToolbarFace(bool leads)`:
     When `leads == true`, show `EditorAiChatBtn` (or `AiChatBtn`) on the far right of the editor ribbon.
     When `leads == false`, collapse them and restore standard PDF editor buttons.

---

## Verification Checklist:
1. **Citation Badges & Fallback:** Click every citation badge in Sidechat answers across PDF and Web modes. Verify every badge scrolls to the source and highlights text. If the model paraphrased the quote, verify the chunk fallback highlights and navigates without failing.
2. **Web Multi-Turn Chat:** Open a webpage in Browser. Ask an initial question, then ask a follow-up question ("where was he born?"). Verify the AI answers accurately using the webpage text on all follow-up turns without claiming the answer is missing.
3. **Streaming Appearance:** Ask a question in Sidechat. Verify the text streams cleanly as markdown prose without raw JSON braces (`{"answer": ...}`) visible at any point.
4. **Auto-Scroll on Send:** Send a question in AI Sidechat. Verify the chat immediately scrolls to the bottom so the question and pending reply are visible. Verify it stays scrolled down during streaming unless the user manually scrolls up.
5. **Clean Citation Formatting:** Verify no answers contain `[ , ]` or double spaces before punctuation. Check multi-turn conversation history to ensure stripped markers leave clean grammatical text.
6. **Text Editor AI Input & Scrollbar:** Open the floating AI bubble. Type multiple lines into the prompt. Verify the textarea expands in height up to max-height, and when scrolling, it uses a thin 5px sleek dark scrollbar rather than a chunky white bar. Press Enter: verify popup closes and input clears.
7. **Table Icon:** Check the Text Editor ribbon. Verify `TableBtn` displays a clear table grid icon rather than a calendar.
8. **Footer Bar Isolation:** Switch to Text Editor and Browser. Confirm the A4 size badge, view mode radios, and zoom combo disappear from the footer bar. Verify clicking the status bar shows the editor/web document info.
9. **Ribbon AI Chat Button:** Verify the `AI Chat` button appears on the far right in Browser mode (alongside Summary) and in Text Editor mode, and clicking it toggles the AI Chat panel.
10. **Rich Text AI Insertion:** Instruct the AI in Text Editor to bold terms. Confirm `**text**` appears formatted as bold in the editor rather than showing raw asterisks.
