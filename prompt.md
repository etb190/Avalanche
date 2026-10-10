# TASK: TextEditor Refinements, Bug Fixes & Streaming (v1.19.79)

Repository: `https://github.com/etb190/Avalanche`  
Target Files:
- `Controls/TextEditorControl.xaml`
- `Controls/TextEditorControl.xaml.cs`
- `Controls/TextEditorDocument.cs`
- `MainWindow.xaml.cs` (if sidebar rail wiring requires adjustment)

---

## 1. Fix Cycle Line Spacing Icon (MDL2 Parity)

### Issue:
`SpacingBtn` in `Controls/TextEditorControl.xaml` currently renders a raw Unicode up-down arrow `&#x2195;` inside a TextBlock, which looks broken and does not match the Segoe MDL2 icon family used by all other toolbar buttons.

### Fix:
In `Controls/TextEditorControl.xaml`:
Change `SpacingBtn` to use the standard Segoe MDL2 line spacing glyph `&#xE8D2;` (LineSpacing):
```xml
<Button x:Name="SpacingBtn" Content="&#xE8D2;" Style="{StaticResource EditorBtn}"
        Click="SpacingBtn_Click" ToolTip="{DynamicResource Str_TT_EditorSpacing}"/>
```
Remove the inner `<TextBlock Text="&#x2195;"/>`. By setting `Content="&#xE8D2;"` directly on the button, it inherits `FontFamily="Segoe MDL2 Assets"` and `FontSize="16"` from `EditorBtn`.

---

## 2. Fix Quotes Behavior (Inline Quote Selection vs Blockquote)

### Issue:
When a portion of text within a paragraph is selected and the user clicks Quote or presses `Ctrl+Shift+B`, Quill converts the entire paragraph into a blockquote instead of quoting only the selected words.

### Fix:
In `Controls/TextEditorDocument.cs`, update `quote()` to distinguish between an active text selection and a resting caret:
```javascript
function quote(){
  var sel = quill.getSelection();
  if (sel && sel.length > 0){
    var text = quill.getText(sel.index, sel.length);
    // If selected text is already wrapped in curly or straight quotes, toggle quotes off
    if ((text.startsWith('“') && text.endsWith('”')) || (text.startsWith('"') && text.endsWith('"'))){
      var unquoted = text.slice(1, -1);
      quill.deleteText(sel.index, sel.length, 'user');
      quill.insertText(sel.index, unquoted, 'user');
      quill.setSelection(sel.index, unquoted.length, 'user');
    } else {
      // Wrap selection in typographic curly quotation marks
      var quoted = '“' + text + '”';
      quill.deleteText(sel.index, sel.length, 'user');
      quill.insertText(sel.index, quoted, 'user');
      quill.setSelection(sel.index, quoted.length, 'user');
    }
    reportState();
    return;
  }
  // When no text is selected (caret resting), toggle blockquote on the current block
  var f = quill.getFormat();
  quill.format('blockquote', !f.blockquote, 'user');
  reportState();
}
```

---

## 3. Remove Redundant Header Type (Keep Only H1 and H2)

### Issue:
There are currently three header buttons: `HeaderBtn` (`H`), `H1Btn` (`H1`), and `H2Btn` (`H2`). Only `H1` and `H2` are wanted.

### Fix:
1. **`Controls/TextEditorControl.xaml`**:
   Remove `HeaderBtn` (`H`). Keep only `H1Btn` (`H1`) and `H2Btn` (`H2`):
   ```xml
   <ToggleButton x:Name="H1Btn" Style="{StaticResource EditorToggle}"
                 Click="H1Btn_Click" ToolTip="{DynamicResource Str_TT_EditorHeader1}">
       <TextBlock Text="&#x48;1" FontWeight="Bold" FontSize="15" FontFamily="Georgia"/>
   </ToggleButton>
   <ToggleButton x:Name="H2Btn" Style="{StaticResource EditorToggle}"
                 Click="H2Btn_Click" ToolTip="{DynamicResource Str_TT_EditorHeader2}">
       <TextBlock Text="&#x48;2" FontWeight="Bold" FontSize="13" FontFamily="Georgia"/>
   </ToggleButton>
   ```
2. **`Controls/TextEditorControl.xaml.cs`**:
   - Remove `HeaderBtn_Click`.
   - Update `H1Btn_Click` to apply level 1: `Post(new { cmd = "header", level = 1 }); RefocusEditor();`
   - Update `H2Btn_Click` to apply level 2: `Post(new { cmd = "header", level = 2 }); RefocusEditor();`
   - Update `reportState` handler in `TextEditorControl.xaml.cs`:
     ```csharp
     int h = PropInt(r, "h");
     SetToggle(H1Btn, h == 1);
     SetToggle(H2Btn, h == 2);
     ```

---

## 4. Fix Caret Reversal Bug ("dog" -> "god") & Smart Typing

### Issue:
Typing sometimes enters characters in reverse order (e.g. typing "dog" produces "god") because the keydown interceptor in `TextEditorDocument.cs`:
1. Used the faulty regex `/[.!?'\s]+$/`, matching *any* space and firing on every single word.
2. Intercepted the keystroke and called `quill.insertText(...)` or `quill.updateContents(...)` **without advancing Quill's selection**. Because Quill does not move the caret automatically on synthetic inserts, the caret remained at the original offset, causing subsequent keystrokes to insert before previously typed letters.

### Fix:
In `Controls/TextEditorDocument.cs`, fix the smart typing listener:
```javascript
quill.root.addEventListener('keydown', function(e){
  if (e.ctrlKey || e.metaKey || e.altKey || e.isComposing) return;
  if (e.key.length !== 1) return;
  var sel = quill.getSelection();
  if (!sel || sel.length) return;
  var idx = sel.index;
  var before = quill.getText(0, idx);

  // 1. Autocorrect standalone lowercase 'i' to 'I ' when followed by space
  if (e.key === ' ' && /(^|\s)i$/.test(before)){
    e.preventDefault();
    quill.updateContents({ ops: [ { retain: idx - 1 }, { delete: 1 }, { insert: 'I ' } ] }, 'user');
    quill.setSelection(idx + 1, 0, 'silent');
    reportState();
    return;
  }

  // 2. Auto-capitalize sentence start: at start of block or after [.!?] + spaces
  if (/^[a-z]$/.test(e.key)){
    var lineInfo = quill.getLine(idx);
    var atBlockStart = !!(lineInfo && lineInfo[1] === 0);
    var afterSentencePunct = /[.!?]\s+$/.test(before);

    if (atBlockStart || afterSentencePunct){
      e.preventDefault();
      var upper = e.key.toUpperCase();
      quill.insertText(idx, upper, 'user');
      quill.setSelection(idx + 1, 0, 'silent');
      reportState();
    }
  }
}, true);
```

---

## 5 & 6. Fix Escaped Quotes (`\"`) and Raw JSON Leak (`{"rewritten_text": ...}`)

### Issue:
AI models frequently output JSON structures (e.g. `{"rewritten_text": "..."}`) with escaped quotes (`\"Winterbust\"`) or markdown fences. The text editor was directly inserting the raw unparsed JSON string into Quill.

### Fix:
In `Controls/TextEditorControl.xaml.cs`, implement clean extraction in `RunAiSelectionAsync` before dispatching to the document:
```csharp
private static string CleanAiGeneratedText(string raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
    string text = raw.Trim();

    // Strip reasoning tags (<think>...</think>) from thinking models
    int thinkStart = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
    if (thinkStart >= 0)
    {
        int thinkEnd = text.IndexOf("</think>", thinkStart, StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
            text = (text[..thinkStart] + text[(thinkEnd + 8)..]).Trim();
    }

    // Strip markdown code fences if wrapped
    if (text.StartsWith("```", StringComparison.Ordinal))
    {
        int firstLine = text.IndexOf('\n');
        if (firstLine >= 0) text = text[(firstLine + 1)..].Trim();
        if (text.EndsWith("```", StringComparison.Ordinal))
            text = text[..^3].Trim();
    }

    // Parse JSON if output was emitted as a JSON object
    if (text.StartsWith('{') && text.EndsWith('}'))
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string[] candidateProps = { "rewritten_text", "rewrittenText", "text", "result", "corrected_text", "output", "content" };
            foreach (var prop in candidateProps)
            {
                if (root.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    text = val.GetString() ?? string.Empty;
                    break;
                }
            }
        }
        catch { /* fallback to string parsing */ }
    }

    // Unescape literal escaped quotes \" -> "
    if (text.Contains("\\\""))
    {
        text = text.Replace("\\\"", "\"");
    }

    // Strip wrapping outer quotes if the entire text was encapsulated in quotes
    if ((text.StartsWith('"') && text.EndsWith('"') && text.Length >= 2) ||
        (text.StartsWith('“') && text.EndsWith('”') && text.Length >= 2))
    {
        text = text[1..^1].Trim();
    }

    return text.Trim();
}
```

---

## 7. Streaming Rewriter with Smooth Typewriter Effect

### Issue:
The user has to wait for completions to finish; rewrites should stream with a smooth typing animation similar to `SummaryWindow`'s typewriter.

### Implementation:
1. **Streaming in `Controls/TextEditorControl.xaml.cs`**:
   - For `RunAiSelectionAsync`, if `config.ProviderType` is OpenAI-compatible, send HTTP request with `stream: true`.
   - Read SSE chunks (`data: {"choices":[{"delta":{"content":"..."}}]}`).
   - Post incremental chunks to WebView2:
     ```csharp
     Post(new { cmd = "aiStreamDelta", seq, delta });
     ```
   - On completion:
     ```csharp
     Post(new { cmd = "aiStreamDone", seq, ok = true });
     ```
2. **Typewriter Queue in `Controls/TextEditorDocument.cs`**:
   - Mirror `SummaryWindow`'s 20ms adaptive buffer:
     ```javascript
     var streamQueue = '';
     var streamTargetIdx = 0;
     var streamTimer = null;

     function onAiStreamDelta(delta){
       streamQueue += delta;
       if (!streamTimer) streamTimer = setInterval(pumpTypewriter, 20);
     }

     function pumpTypewriter(){
       if (!streamQueue.length){
         if (streamDonePending){
           clearInterval(streamTimer);
           streamTimer = null;
           finishAiStream();
         }
         return;
       }
       // Adaptive pacing: 1 to 10 chars per tick based on backlog
       var step = Math.max(1, Math.min(10, Math.floor((streamQueue.length + 5) / 6)));
       var slice = streamQueue.slice(0, step);
       streamQueue = streamQueue.slice(step);
       quill.insertText(streamTargetIdx, slice, 'user');
       streamTargetIdx += slice.length;
       quill.setSelection(streamTargetIdx, 0, 'silent');
     }
     ```

---

## 8. Toolbar Placement: Move Save and Open Next to New and Before Browser

### Issue:
Save and Open buttons were buried in `EditorTabBand` (which is invisible when only one document is open). They belong on the **main editor ribbon** before the `Browser` button.

### Fix:
1. **`Controls/TextEditorControl.xaml`**:
   Move `New`, `Open`, and `Save` buttons into the editor toolbar stack panel at `Grid.Row="0"` immediately before `EditorBrowserBtn`:
   ```xml
   <StackPanel Orientation="Horizontal" VerticalAlignment="Center" HorizontalAlignment="Center" Margin="8,0,8,0">
       <!-- File operations: New, Open, Save -->
       <Button x:Name="EditorNewBtn" Content="&#xE7C3;" Style="{StaticResource EditorBtn}"
               Click="EditorNewBtn_Click" ToolTip="{DynamicResource Str_TT_EditorNewTab}"/>
       <Button x:Name="EditorOpenBtn" Content="&#xE8DA;" Style="{StaticResource EditorBtn}"
               Click="EditorOpenBtn_Click" ToolTip="{DynamicResource Str_Editor_OpenDoc}"/>
       <Button x:Name="EditorSaveBtn" Content="&#xE74E;" Style="{StaticResource EditorBtn}"
               Click="EditorSaveBtn_Click" ToolTip="{DynamicResource Str_Editor_SaveDoc}"/>
       <Rectangle Width="1" Fill="{DynamicResource CardBorderBrush}" Margin="6,8"/>

       <!-- Destination switches: Browser, PDF Editor -->
       <Button x:Name="EditorBrowserBtn" Content="&#xE774;" Style="{StaticResource EditorBtn}"
               Click="EditorBrowserBtn_Click" ToolTip="{DynamicResource Str_TT_EditorBrowser}"/>
       <Button x:Name="EditorPdfBtn" Content="&#xE8A5;" Style="{StaticResource EditorBtn}"
               Click="EditorPdfBtn_Click" ToolTip="{DynamicResource Str_TT_EditorPdf}"/>
       <Rectangle Width="1" Fill="{DynamicResource CardBorderBrush}" Margin="6,8"/>
   ...
   ```
2. In `EditorTabBand`: Keep or streamline `EditorTabNewBtn`, but ensure primary access is on the ribbon toolbar.
3. In `Controls/TextEditorControl.xaml.cs`: Wire `EditorNewBtn_Click`, `EditorOpenBtn_Click`, and `EditorSaveBtn_Click` to `OpenNewTab()`, `EditorTabOpenBtn_Click()`, and `EditorTabSaveBtn_Click()`.

---

## 9. Fix Broken Thumbnails Showing Only "Page 1"

### Issue:
`postThumbs()` in `TextEditorDocument.cs` attempts to rasterize an SVG `<foreignObject>` onto an HTML5 canvas and call `c.toDataURL()`. In Chromium / WebView2, `<foreignObject>` taints the canvas, throwing a security exception (`SecurityError: Tainted canvases may not be exported`). The exception is caught and emits `thumbs: ['']`, causing `MainWindow.xaml.cs` to fall back to a blank card displaying only the label "Page 1".

### Fix:
In `Controls/TextEditorControl.xaml.cs`, use WebView2's native, reliable capture API (`CapturePreviewAsync`):
```csharp
public async Task RefreshThumbnailsAsync()
{
    if (_webView?.CoreWebView2 == null) return;
    try
    {
        using var ms = new MemoryStream();
        await _webView.CoreWebView2.CapturePreviewAsync(
            Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, ms);
        byte[] png = ms.ToArray();
        string dataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
        _thumbCache[_tabs[_activeTab]] = new[] { dataUrl };
        ThumbsChanged?.Invoke(new[] { dataUrl });
    }
    catch
    {
        // Fail gracefully without crashing
    }
}
```
Trigger `RefreshThumbnailsAsync()` when documents load, switch, or after the edit idle timer fires.

---

## Verification Checklist

1. [ ] Spacing button displays the clean Segoe MDL2 icon `&#xE8D2;` matching all other toolbar buttons.
2. [ ] Quoting a selection wraps the selected words in `“...”` instead of converting the entire paragraph into a blockquote.
3. [ ] Toolbar has only `H1` and `H2` buttons (`H` removed).
4. [ ] Typing words like "dog" preserves proper forward caret advancement and never reverses text to "god".
5. [ ] Rewritten text never contains escaped backslash quotes (`\"Winterbust\"`).
6. [ ] Rewritten text never outputs raw JSON objects (`{"rewritten_text": ...}`).
7. [ ] Rewrites stream smoothly into Quill with a letter-by-letter typing effect.
8. [ ] New, Open, and Save buttons are positioned in the top toolbar to the left of the Browser button.
9. [ ] Sidebar rail renders real page thumbnails instead of empty cards showing only "Page 1".
10. [ ] Project builds with 0 errors.
