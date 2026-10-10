# TASK: Table Layout & Newlines, Summarizer Scroll Seizure Fix, Ribbon Layout, Sidechat Streaming & Citation Formatting

Repository: `https://github.com/etb190/Avalanche`  
Target Files:
- `Controls/TextEditorDocument.cs`
- `Controls/TextEditorControl.xaml`
- `Controls/TextEditorControl.xaml.cs`
- `Features/Summary/SummaryWindow.xaml.cs`
- `Features/Summary/WebSummaryWindow.xaml.cs`
- `Features/AI/AiMarkdown.cs`
- `Features/AI/AiChatViewModel.cs`
- `Features/AI/OpenAiCompatibleProvider.cs`
- `Features/AI/AiPromptLibrary.cs`
- `Features/AI/prompts.default.json`
- `Features/AI/WebChat.cs`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `Shell/SettingsPanel.cs`

---

## 1. Text Editor: Table Width Distortion & Layout Stability

### Problem:
When inserting or typing inside a table in the Text Editor, typing even a short amount of text in a cell dramatically distorts the table's width. The column balloons out and pushes adjacent columns to their minimum width, and the table stretches wider than the document margins.

### Root Cause:
In `Controls/TextEditorDocument.cs`:
```css
.ql-editor table { border-collapse: collapse; width: 100%; margin: 16px 0; table-layout: auto; }
.ql-editor td, .ql-editor th { border: 1px solid #d0d7de; padding: 8px 12px; min-width: 48px; vertical-align: top; box-sizing: border-box; }
```
1. `table-layout: auto`: With automatic table layout, browsers dynamically recalculate column widths on every keystroke based on content length. Typing a single sentence into Column 1 forces Column 1 to expand to 80%+ of the table width while shrinking Columns 2 and 3 down to `min-width: 48px`.
2. Lack of `word-break` and `overflow-wrap`: Words or text without explicit wrapping expand the table beyond the 816px canvas width.
3. Excessive cell padding: `padding: 8px 12px` consumes 24px of horizontal padding per column, compounding width pressure.

### Fix:
In `Controls/TextEditorDocument.cs`:
Update the table CSS styles:
```css
.ql-editor table {
  border-collapse: collapse;
  width: 100%;
  max-width: 100%;
  margin: 16px 0;
  table-layout: fixed;
  box-sizing: border-box;
}
.ql-editor td, .ql-editor th {
  border: 1px solid #d0d7de;
  padding: 6px 10px;
  vertical-align: top;
  box-sizing: border-box;
  word-break: break-word;
  overflow-wrap: break-word;
}
body.az-inv .ql-editor td, body.az-inv .ql-editor th {
  border-color: #444c56;
}
.ql-editor td:focus, .ql-editor th:focus {
  outline: 1.5px solid #4a90d9;
  outline-offset: -1px;
}
```
With `table-layout: fixed; width: 100%; max-width: 100%;`, all columns maintain an equal, fixed share of the table width by default, and typing inside any cell never expands or shifts column widths.

---

## 2. Text Editor: Enter Inside Table Cell Pushes Entire Table Down

### Problem:
Pressing `Enter` while inside a table cell does not create a new line within the cell. Instead, it pushes the entire table down (or exits/splits the table).

### Root Cause:
In Quill.js v2, table cells are represented as block blots (`class TableCell extends Block { static tagName = "TD"; }`). Quill's default keyboard module includes a `"table enter"` binding that explicitly jumps outside the table:
```javascript
"table enter": {
  key: "Enter",
  format: ["table"],
  handler(range) {
    // Inserts a newline outside before or after the table, exiting the table!
  }
}
```
Because Quill's native block model treats `\n` as a block boundary, pressing Enter either triggers this exit logic or creates a new block blot outside `<tr>`, splitting the table and pushing it down.

### Fix:
In `Controls/TextEditorDocument.cs`:
1. Register a custom inline embed blot (`line-break`) representing a soft `<br>`:
   ```javascript
   var Embed = Quill.import('blots/embed');
   class LineBreakBlot extends Embed {
     static blotName = 'line-break';
     static tagName = 'BR';
   }
   Quill.register(LineBreakBlot);
   ```
2. Intercept `Enter` and `Shift+Enter` when inside a table cell:
   ```javascript
   quill.keyboard.addBinding({
     key: 'Enter',
     shiftKey: null,
     format: ['table']
   }, function(range, context) {
     quill.insertEmbed(range.index, 'line-break', true, 'user');
     quill.setSelection(range.index + 1, 'silent');
     reportState();
     return false; // prevent default Quill table exit
   });
   ```
   Add a matching keydown handler on `quill.root` capture phase to ensure standard `Enter` and `Shift+Enter` inside any `td` / `th` reliably insert the `<br>` without breaking or pushing the table.

---

## 3. Summarizer Window: Fix Violent Scrolling Seizure / Flashing During Streaming

### Problem:
When reading a summary as it streams in `SummaryWindow` or `WebSummaryWindow`, scrolling down causes severe jitter and flickering. The window violently jerks back and forth between the top (position 0) and the user's scroll position like a seizure.

### Root Cause:
In `Features/AI/AiMarkdown.cs` (`Render` method):
```csharp
double keep = rtb.VerticalOffset;
bool rideBottom = rtb.ViewportHeight > 0
    && rtb.ExtentHeight > rtb.ViewportHeight
    && keep >= rtb.ExtentHeight - rtb.ViewportHeight - 2.0;

var doc = BuildDocument(text, parse, rtb, GetParagraphAlignment(rtb));
rtb.Document = doc; // Destroys visual tree and resets VerticalOffset to 0!

if (keep > 0 || rideBottom)
{
    rtb.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
    {
        try
        {
            if (!rideBottom && rtb.VerticalOffset > 0.5) return;   // reader moved on their own
            double max = Math.Max(0, rtb.ExtentHeight - rtb.ViewportHeight);
            rtb.ScrollToVerticalOffset(rideBottom ? max : Math.Min(keep, max));
        }
        catch { }
    }));
}
```
1. `_typeTimer` ticks every 20ms (50 FPS).
2. On every tick, `rtb.Document = doc` assigns a brand-new `FlowDocument`. In WPF, replacing `Document` immediately drops `VerticalOffset` to 0.
3. The asynchronous `BeginInvoke(DispatcherPriority.Loaded)` attempts to restore the offset later, but by the time it runs, another 20ms tick has already captured `keep = 0` (or `rtb.VerticalOffset > 0.5` causes it to abort).
4. The scroll position rapidly oscillates between 0 and the user's scroll offset 50 times per second, creating a violent visual strobe effect.

### Fix:
In `Features/AI/AiMarkdown.cs`, `Features/Summary/SummaryWindow.xaml.cs`, and `Features/Summary/WebSummaryWindow.xaml.cs`:
1. **Track Intentional User Scroll Offset:**
   - In `SummaryWindow` and `WebSummaryWindow`, listen to user scroll input (e.g. `PreviewMouseWheel` or user drag) and record `_userDesiredVerticalOffset`.
   - If the user has manually scrolled down, maintain this offset across renders.
2. **Synchronous Scroll Restoration:**
   - In `AiMarkdown.Render`, do not let `VerticalOffset` stay at 0 across asynchronous frames.
   - After `rtb.Document = doc`, call `rtb.UpdateLayout()` and immediately restore `rtb.ScrollToVerticalOffset(keep)` synchronously within the same render pass.
   - If the user was riding the bottom, scroll to bottom (`rtb.ScrollToEnd()`). If the user scrolled mid-page, strictly restore `keep`.
3. **Throttle Streaming Markdown Rebuilds:**
   - While streaming, update the full markdown AST at a throttled cadence (e.g. 100–120ms or upon paragraph completion) rather than recreating a 5-page `FlowDocument` visual tree every 20 milliseconds.

---

## 4. Ribbon Toolbar: Move Browser Summary Icon to the Far Right

### Problem:
The browser summary ribbon button (`WebSumBtn`) is currently sitting on the far left next to `NewFileBtn`, `TextEditorBtn`, and `PdfEditorBtn`. The user expects it on the far right alone, across from the PDF editor icon, matching standard toolbar layout.

### Fix:
1. In `MainWindow.xaml`:
   - Move `WebSumBtn` out of `LeftBar` / `GrpToolsToggle`.
   - Place `WebSumBtn` on the far right end of the ribbon toolbar (inside `RightContainer` or a dedicated right-aligned container that is visible when browser leads).
   ```xml
   <Button x:Name="WebSumBtn" Content="&#xE8A5;" Style="{StaticResource ToolbarButton}"
           Click="WebSumBtn_Click" ToolTip="{DynamicResource Str_TT_WebSummarize}"
           Visibility="Collapsed"/>
   ```
2. In `Shell/SettingsPanel.cs` (`ApplyBrowserToolbarFace`):
   - When `leads == true`:
     - Keep `NewFileBtn`, `TextEditorBtn`, and `PdfEditorBtn` visible on the left in `LeftBar`.
     - Show `WebSumBtn` on the far right.
     - Ensure `RightContainer` or the right-aligned container hosting `WebSumBtn` is set to `Visibility = Visibility.Visible` while hiding unrelated PDF annotation tools.
   - When `leads == false`:
     - Hide `WebSumBtn`.

---

## 5. Fix AI Sidechat Streaming Failure (Text Appearing All at Once)

### Problem:
In all AI Sidechats (PDF Sidechat, Web Sidechat, Editor Sidechat), answers do not stream progressively. The user sees a loading state for 20–60 seconds, and then the entire markdown text appears all at once.

### Root Causes:
1. **Low Dispatcher Priority (`DispatcherPriority.Background`):**
   In `Features/AI/AiChatViewModel.cs` (`StreamReplyIntoAsync`):
   - `paintTimer` is created with `DispatcherPriority.Background` (priority 4).
   - In WPF, `Background` priority is lower than layout and rendering. When network chunks arrive rapidly, `paintTimer` gets starved and does not tick until the background streaming task completes.
2. **Message Border Collapsed by `IsLoading`:**
   In `MainWindow.xaml`:
   ```xml
   <DataTrigger Binding="{Binding IsLoading}" Value="True">
       <Setter Property="Visibility" Value="Collapsed"/>
   </DataTrigger>
   ```
   In `StreamReplyIntoAsync`:
   ```csharp
   ui.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
   {
       assistantMsg.IsLoading = false;
   }));
   ```
   Because `assistantMsg.IsLoading = false` is also dispatched at `Background` priority, `MessageBorder` remains `Collapsed` during streaming. The message bubble is completely hidden until generation finishes and `IsLoading = false` is set at the end of `GenerateReplyAsync`.
3. **Missing `Accept: text/event-stream` Header:**
   In `Features/AI/OpenAiCompatibleProvider.cs` (`GetChatCompletionStreamAsync`):
   The request sends `body["stream"] = true` but fails to include `request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"))`. Cloud gateways, proxies, and endpoints buffer the response and return `application/json` instead of streaming SSE frames.

### Fix:
1. In `Features/AI/AiChatViewModel.cs` (`StreamReplyIntoAsync`):
   - Change `paintTimer` priority to `DispatcherPriority.Render` (or `Normal`):
     ```csharp
     var paintTimer = new System.Windows.Threading.DispatcherTimer(
         TimeSpan.FromMilliseconds(24), DispatcherPriority.Render,
         (_, _) =>
         {
             if (!dirty) return;
             dirty = false;
             string next = StripThinkSpans(raw.ToString());
             if (string.Equals(next, visible, StringComparison.Ordinal)) return;
             visible = next;
             assistantMsg.Content = visible;
         },
         ui);
     ```
   - On the first chunk received (`if (!any)`), immediately set `assistantMsg.IsLoading = false` at `DispatcherPriority.Normal` (or `Render`) so the message bubble becomes visible instantly:
     ```csharp
     if (!any)
     {
         any = true;
         ui.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
         {
             assistantMsg.IsLoading = false;
         }));
     }
     ```
2. In `Features/AI/OpenAiCompatibleProvider.cs`:
   - In `GetChatCompletionStreamAsync`, explicitly add the SSE accept header:
     ```csharp
     request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
     ```

---

## 6. Inline Evidence Citations: Never Cluster Citations at the End of Paragraphs

### Problem:
In AI Sidechat answers, the model places all citation markers clustered together at the very end of the paragraph/answer (e.g. `...in 2017. (1) (2) (3) (4) (5)`), instead of attaching individual citations directly after their specific factual claims (e.g. `Lee Martin McDonald. (1) ...published in 2017. (2)`).

### Root Cause:
In `Features/AI/prompts.default.json` (`sidechat_standard` and `websidechat_standard`) and `AiPromptLibrary.cs`, the prompt instructs the model to provide citations, but lacks an explicit negative constraint prohibiting end-of-paragraph citation clustering. LLMs default to dumping citations in a single batch at the end.

### Fix:
Update the citation instructions in `Features/AI/prompts.default.json` (`sidechat_standard` and `websidechat_standard`), `AiPromptLibrary.cs`, and `WebChat.cs`:
Add a strict, prominent citation mandate:
```text
CRITICAL CITATION MANDATE — IMMEDIATE INLINE PLACEMENT:
- Every factual claim, date, name, statistic, or finding MUST have its supporting citation marker [SOURCE_n] placed IMMEDIATELY after that specific clause or sentence.
- NEVER cluster or batch citations together at the end of a sentence, paragraph, or answer (e.g. NEVER write "...end of text. [SOURCE_1] [SOURCE_2] [SOURCE_3]").
- Example of CORRECT placement:
  "The author of this book is Lee Martin McDonald. [SOURCE_1] The current fourth edition was published in 2017 by Bloomsbury T&T Clark. [SOURCE_2] The first edition was published by Abingdon Press in 1988. [SOURCE_3]"
- Example of FORBIDDEN placement:
  "The author is Lee Martin McDonald and the fourth edition was published in 2017 by Bloomsbury T&T Clark, following earlier editions by Abingdon Press and Hendrickson Publishers. [SOURCE_1] [SOURCE_2] [SOURCE_3]"
- Use exact ASCII square brackets [SOURCE_n] only.
```

---

## 7. Fix Erroneous "Merge PDFs" Button Label in Text Editor Toolbar Ribbon

### Problem:
In the Text Editor ribbon toolbar, the first button displays the label **"Merge PDFs"** under a `+` icon:
`[+ Merge PDFs] [Open] [Save] [Browser] [PDF Editor]`.
The Text Editor has nothing to do with merging PDFs.

### Root Cause:
In `Shell/SettingsPanel.cs`:
`_toolbarLabelKeys` maps Segoe MDL2 glyph strings to localization string resource keys:
```csharp
private static readonly Dictionary<string, string> _toolbarLabelKeys = new()
{
    ["\uE710"] = "Str_Lbl_New",       // Line 727
    ...
    ["\uE710"] = "Str_Lbl_Merge",     // Line 734: OVERWRITES Line 727!
};
```
Because `_toolbarLabelKeys` is a dictionary literal, the entry `["\uE710"] = "Str_Lbl_Merge"` silently overwrites `["\uE710"] = "Str_Lbl_New"`.
Both `NewFileBtn` and `MergeBtn` share the glyph `\uE710` (the plus sign).
When `IndexToolbarButtons()` indexes toolbar buttons, `_toolbarLabelKeys["\uE710"]` evaluates to `"Str_Lbl_Merge"`.
Consequently, `NewFileBtn` (which creates a new document) is labeled with `"Merge PDFs"`! In Text Editor mode, `MergeBtn` was already collapsed, but `NewFileBtn` was visible and mistakenly wearing the "Merge PDFs" label.

### Fix:
In `Shell/SettingsPanel.cs`:
1. In `IndexToolbarButtons()`:
   Identify `NewFileBtn` and `MergeBtn` by reference equality rather than ambiguous glyph lookup:
   ```csharp
   if (ReferenceEquals(btn, NewFileBtn))
       _toolbarButtons.Add((btn, "\uE710", "Str_Lbl_New"));
   else if (ReferenceEquals(btn, MergeBtn))
       _toolbarButtons.Add((btn, "\uE710", "Str_Lbl_Merge"));
   ```
2. In `_toolbarLabelKeys`:
   Ensure `["\uE710"] = "Str_Lbl_New"` is not clobbered by `MergeBtn`. If needed, differentiate `MergeBtn` or keep reference-based indexing so `NewFileBtn` always resolves to `Str_Lbl_New`.
3. Verify that in Text Editor mode, `NewFileBtn` displays the label `"New"` (or localized equivalent), and `MergeBtn` remains collapsed.

---

## Verification & Testing Checklist:
1. **Table Width & Layout:** Insert a table in Text Editor. Type short and long sentences in cells. Confirm table width stays strictly within page bounds and columns do not distort or collapse neighboring columns.
2. **Table Newline:** Place cursor inside a table cell and press `Enter`. Confirm a new line is inserted within the cell without pushing or splitting the table. Test `Shift+Enter` as well.
3. **Summarizer Scroll Stability:** Run a 500-word summary in PDF and Web summarizers. While text is streaming, scroll down halfway. Confirm the scroll position stays smooth and stable with zero jumping, strobe flickering, or rubber-banding to the top.
4. **Browser Ribbon Layout:** Open the Web Browser pane. Confirm the Web Summary button is alone on the far right of the ribbon toolbar, across from the PDF Editor button.
5. **AI Sidechat Streaming:** Ask questions in PDF Sidechat, Web Sidechat, and Text Editor Sidechat. Confirm tokens stream progressively in real-time into the message bubble without stalling until the end.
6. **Citation Placement:** Ask a multi-fact question in Sidechat (e.g. author and publication history). Confirm each factual statement carries its own `[SOURCE_n]` badge inline immediately after the claim, rather than all citations lumped at the end of the paragraph.
7. **Text Editor Toolbar Button:** Switch to Text Editor. Confirm the first ribbon button displays `+` with the label `"New"`, and `"Merge PDFs"` is not present.
