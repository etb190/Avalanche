# TASK: Replace Custom Text Editor with Embedded Quill.js

Repository: `https://github.com/etb190/Avalanche`
Target Files:
- `Controls/TextEditorDocument.cs`
- `Controls/TextEditorControl.xaml.cs`
- `Controls/TextEditorControl.xaml`
- `Resources/Scripts/` (Add `quill.min.js`, `quill.snow.css` as embedded resources)

---

## 1. Context & Objective

The current text editor in `Controls/TextEditorDocument.cs` is a broken, handwritten 1,280-line `contentEditable` experiment trying to implement multi-page reflow, binary-search DOM text slicing, zero-width space caret hacks, and manual DOM span surgery. It is brittle and unusable.

**Goal:** Scrap the custom DOM manipulation completely and replace the editor engine with an embedded, standalone instance of **Quill.js (v2.0)** running inside the existing WebView2 pane. Keep the UI smooth, lightweight, and working 100% offline.

---

## 2. What Must Be SCRAPPED

In `Controls/TextEditorDocument.cs`:
1. **DELETE all multi-page reflow logic:** Remove `makePage`, `overflow`, `reflowFrom`, `pullFromNext`, `trimTrailing`, `nodeOverflowsAPage`, and `probePage`.
2. **DELETE all zero-width space hacks:** Remove `ZWSP = '\u200B'`, `cleanZwsp`, `isCrutchSpan`.
3. **DELETE manual DOM styling:** Remove `dressRange`, `collectTextNodes`, `deadWrapper`, `normalizeSpans`.
4. **DELETE the custom snapshot undo system:** Remove `undoStack`, `selPaths`, `pushUndo`, `restoreSnap`.
5. **DELETE custom behind-text image dragging:** Remove `imageHitAt`, manual `pointerdown`/`pointermove` coordinate listeners.

---

## 3. Architecture & Setup

1. **Embedded Assets (100% Offline):**
   - Place standalone `quill.min.js` and `quill.snow.css` into `Resources/Scripts/` (marked as `<Resource>` in `Avalanche.csproj`, loaded via pack URI or inlined into the HTML template).
   - No external CDN links; the editor must work without internet.

2. **Document Canvas Styling:**
   - Present a clean, beautiful single-page or pageless document canvas:
     - Outer container has the workspace dark/neutral background (`#3d4046` or theme background).
     - The `.ql-container` / `.ql-editor` is centered, Letter-width (e.g., `max-width: 816px`, `min-height: 1056px`), white background, subtle drop shadow, with standard margins (`padding: 64px 72px`).
   - Scrolling is butter-smooth and seamless across the whole document.

---

## 4. Feature Requirements

### A. Formatting (Fonts, Sizes, Styles)
- **Fonts:** Configure Quill's Font blot to allow standard fonts: `Segoe UI`, `Times New Roman`, `Arial`, `Georgia`, `Calibri`, `Courier New`.
- **Font Sizes:** Configure size support for point sizes (`8pt`, `9pt`, `10pt`, `11pt`, `12pt`, `14pt`, `16pt`, `18pt`, `24pt`, `32pt`).
- **Font Size Stepping (+/-):** Provide helper function to step up or down through the size ladder from the current selection.
- **Styles:** Bold, Italic, Underline, Strikethrough using native `quill.format(...)`.

### B. Hyperlinks
- Link insertion and editing using Quill's native link format:
  - If text is selected: wrap selection in link.
  - If no text selected: prompt/popover to insert link text and URL.
  - Clicking/Ctrl+clicking links sends `{ type: 'link', url: '...' }` to the WPF host.

### C. Images
- Insert images via file dialog or drag-and-drop/paste (base64 Data URI or local blob).
- Support standard inline image sizing and alignment.

### D. Footnotes
- Implement a clean footnote mechanism:
  - Insert a superscript anchor at the caret (e.g. `[^1]`).
  - Maintain a numbered footnote list at the bottom of the document (`<div class="footnotes">`).
  - Clicking a footnote marker scrolls to its bottom entry and vice-versa.

### E. Undo & Redo
- Use Quill's built-in `history` module. Bind Ctrl+Z and Ctrl+Y / Ctrl+Shift+Z natively.

---

## 5. WPF Ribbon <-> WebView2 IPC Protocol

Maintain the clean IPC bridge with WPF:
- **WPF -> Editor (`msg.cmd`):**
  - `'bold'`: `quill.format('bold', !quill.getFormat().bold)`
  - `'italic'`: `quill.format('italic', !quill.getFormat().italic)`
  - `'underline'`: `quill.format('underline', !quill.getFormat().underline)`
  - `'strike'`: `quill.format('strike', !quill.getFormat().strike)`
  - `'font'`: `quill.format('font', msg.name)`
  - `'size'`: `quill.format('size', msg.pt)`
  - `'sizeStep'`: Step size up (+1) or down (-1) based on current format
  - `'linkui'`: Open link popover or format link
  - `'footnote'`: Insert footnote
  - `'image'`: Insert image data URL
  - `'undo'`: `quill.history.undo()`
  - `'redo'`: `quill.history.redo()`
  - `'load'`: Load Delta or HTML content
  - `'dump'`: Request current HTML/Delta content for auto-save

- **Editor -> WPF (`msg.type`):**
  - `'ready'`: Quill initialized
  - `'state'`: Reports current formatting at caret (`font`, `size`, `b`, `i`, `u`, `s`) on `selection-change` to update WPF ribbon toggle states
  - `'save'`: Emits content on debounced change for background session saving
  - `'link'`: Requests host to open clicked URL

---

## 6. Verification Checklist

1. [ ] Solution builds cleanly with 0 errors.
2. [ ] Text editor opens without crashing or lagging.
3. [ ] Typing, selecting, deleting, and pasting are fluid with 0 caret jumping or lost keystrokes.
4. [ ] Bold, Italic, Underline, Font selection, and Size dropdowns update immediately.
5. [ ] Font size +/- buttons step accurately through the size ladder.
6. [ ] Undo (Ctrl+Z) and Redo (Ctrl+Y) work reliably without snapshot memory leaks.
7. [ ] Footnotes and hyperlinks insert cleanly.
8. [ ] Document auto-saves and restores properly on reload.
