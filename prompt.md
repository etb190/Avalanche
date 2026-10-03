# TASK: Fix Summary Navigator Word Hover Performance & Buggy Hit-Testing (Instant O(1) Hit-Testing)

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### Overview & Problem Statement
In Avalanche's Summary Navigator (`SummaryWindow`), hovering over words in the digest text (`DocBox`) to see the green word plate and hand cursor is slow, laggy, and buggy.

#### Root Causes Identified:
1. **Catastrophic Layout Invalidation Loop**:
   - `DigestWordHighlightAdorner` subscribed to `box.LayoutUpdated += (_, _) => _rectsDirty = true;`.
   - In WPF, `LayoutUpdated` fires after **every layout and rendering cycle** (including when `InvalidateVisual()` is called).
   - On every `MouseMove` event, `EnsureRects()` ran: it iterated over all words in the entire document (up to 12,000 words), calling `GetCharacterRect` twice per word (**up to 24,000 text formatting queries on the UI thread on every mouse move**).
   - This caused severe CPU spikes and a 100ms–400ms lag behind pointer movement.
2. **Phantom Multi-Line Bounding Boxes (`Rect.Union`)**:
   - `EnsureRects()` combined `_words[i].Start.GetCharacterRect(...)` and `_words[i].End.GetCharacterRect(...)` with `Rect.Union(start, end)`.
   - In justified text or when hyphenated/wrapped words crossed a line break, `Rect.Union` formed a giant rectangle spanning from the end of line 1 across all intervening space to line 2, causing false positive hover highlights in margins and unrelated lines.
3. **Background Dispatcher Queue Latency**:
   - Word lists were populated asynchronously on `DispatcherPriority.Background`. Until that queue settled, hovering over newly generated or streaming summaries produced zero highlights.
4. **Interference with Text Drag-Selection**:
   - Moving the mouse while drag-selecting text still triggered the single-word hover plate, visually conflicting with the selection.

---

### Solution Architecture: Zero-Overhead On-Demand O(1) Hit-Testing

Eliminate full-document pre-measuring, background queues, and `LayoutUpdated` listeners entirely in `Features/Summary/DigestWordHighlight.cs`. Hit-test strictly on-demand in O(1) time:

```
[MouseMove over DocBox]
       │
       ▼
Is Drag-Selecting or Selection Active? ──Yes──► Clear Hover & Exit
       │ No
       ▼
Get TextPointer under Pointer (snapToText: false)
       │
       ▼
Is Cursor on a Word Character? ─────────No───► Clear Hover & Exit
       │ Yes
       ▼
Is it the Same Word as Current Hover? ──Yes──► Exit Immediately (0 allocations, 0 redraws)
       │ No
       ▼
Measure ONLY this single word's bounding box (< 0.05 ms)
Draw dark green plate & set Cursors.Hand
```

---

### Implementation Details (`Features/Summary/DigestWordHighlight.cs`)

1. **Remove Full-Document Word Walk & Rect Cache**:
   - Remove `_words`, `_rects`, `_rectsDirty`, `_rebuildQueued`, `QueueRebuild()`, `RebuildWords()`, and `EnsureRects()`.
   - Remove the `box.LayoutUpdated` event listener.
2. **On-Demand `OnMouseMove`**:
   - If `e.LeftButton == MouseButtonState.Pressed || !_box.Selection.IsEmpty`: suppress hover and return.
   - Call `_box.GetPositionFromPoint(pt, snapToText: false)`. Passing `snapToText: false` ensures `null` is returned when hovering over margins, padding, or empty space.
   - Call `TryIsolateWord(hit, out TextPointer? start, out TextPointer? end)`. If not over a word character, clear hover.
   - **Early-exit for same word**: If `_hoverStart != null && _hoverEnd != null && _hoverStart.CompareTo(start) == 0 && _hoverEnd.CompareTo(end) == 0`, exit immediately without any allocations or redraws.
   - **Measure only the active word**:
     - Call `rStart = start.GetCharacterRect(LogicalDirection.Forward)` and `rEnd = end.GetCharacterRect(LogicalDirection.Backward)`.
     - If on a single line (`Math.Abs(rStart.Top - rEnd.Top) < 4`): combine into `Rect(minX, minY, width, height)`.
     - If wrapped across lines: bound only the line segment containing `pt.Y`.
     - Verify `Rect.Inflate(wordRect, 3, 2).Contains(pt)`.
   - Update `_hoverStart`, `_hoverEnd`, `_hoverRect`, set `_box.Cursor = Cursors.Hand`, and call `InvalidateVisual()`.
3. **Instant Clear on State Changes**:
   - On `MouseLeave`, `TextChanged`, or `ScrollViewer.ScrollChanged`: call `ClearHover()`, clearing the rect, resetting cursor to `null`, and calling `InvalidateVisual()`.
4. **Drawing**:
   - In `OnRender(DrawingContext dc)`: draw `PlateFill` (`#B31B5E20`) and `PlateEdge` (`#E60F3D14`) with rounded corners (3px radius) inflated by (2.5, 1.5) around `_hoverRect`.

---

### Verification Checklist
1. **Instant Response**: Hovering over words highlights immediately with zero perceptible lag (< 0.1ms per frame).
2. **No Phantom Boxes**: Hovering in margins or near line breaks never highlights distant words or multi-line areas.
3. **No Drag-Selection Conflicts**: Dragging to select text suppresses single-word hover plates.
4. **All Unit Tests Pass**: Run `dotnet test` and confirm all 2,011 unit tests pass.
