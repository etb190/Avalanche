using System;
using System.IO;
using System.Windows;

namespace Avalanche.Controls
{
    // The text editor's page (v1.19.72): one embedded HTML document carrying a
    // standalone Quill.js v2 writing surface. The handwritten pagination engine
    // is gone - makePage, overflow, reflowFrom, pullFromNext, trimTrailing, the
    // zero-width caret crutches, the snapshot undo, the DOM span surgery - and
    // the sheet is pageless now: one white Letter canvas, centered on the
    // workspace ground, that grows as the writing does. Quill owns the document
    // model, the caret and the history; the sheet adds what Quill does not
    // ship: footnotes with a numbered, click-jumping list at the foot, a link
    // popover, image paste and drop, a page raster for the sidebar's rail, and
    // an image life after insert (v1.19.76): click to select, drag to move,
    // corner handles to resize, Delete to remove.
    // Deliberately 100% offline: Quill rides as WPF resources
    // (Resources/Scripts/quill.min.js + quill.snow.css) and is inlined into the
    // document at first use - no CDN, ever.
    // Communication contract with the ribbon:
    //   in  {cmd:...}  bold|italic|underline|strike|font|size|sizeStep|linkui|
    //                  footnote|image|undo|redo|load|dump|scroll|focus|i18n|
    //                  header|bullet|number|subnumber|quote|selectAll|copy|
    //                  cut|paste|pasteImage
    //   out {type:...} ready|state|save|link|title|pages|thumbs|clip
    public static class TextEditorDocument
    {
        private static string? _assembled;

        /// <summary>The embedded editor document: the template below with
        /// Quill's engine and theme inlined from their WPF resources.</summary>
        public static string Html => _assembled ??= Assemble();

        private static string Assemble()
        {
            string js = ReadResource("Resources/Scripts/quill.min.js");
            string css = ReadResource("Resources/Scripts/quill.snow.css");
            return Template.Replace("__QUILL_CSS__", css).Replace("__QUILL_JS__", js);
        }

        // The same ride Readability takes (WebBrowserControl): a WPF <Resource>
        // read through the pack URI at first use, empty when the resource is
        // missing so a packaging slip degrades to a blank sheet, never a crash.
        private static string ReadResource(string path)
        {
            try
            {
                var info = Application.GetResourceStream(new Uri("pack://application:,,,/" + path));
                if (info?.Stream is null) return string.Empty;
                using var reader = new StreamReader(info.Stream, System.Text.Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch
            {
                return string.Empty;
            }
        }

        public const string Template = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>__QUILL_CSS__</style>
<style>
  html, body { margin:0; padding:0; }
  body { background:#3d4046; overflow-x:hidden; font-family:'Segoe UI',sans-serif; }
  #canvas { width:816px; min-height:1056px; margin:24px auto 48px auto; background:#ffffff;
            box-shadow:0 2px 10px rgba(0,0,0,0.45); border-radius:2px; box-sizing:border-box; }
  /* The snow theme pins the container to height:100% and gives the editor its
     own scrollbar - a pageless sheet wants the opposite: the canvas grows,
     the window scrolls, one unbroken ride. */
  .ql-container { height:auto; font-family:'Times New Roman',serif; }
  .ql-editor { height:auto; min-height:1056px; overflow-y:visible;
               font-family:'Times New Roman',serif; font-size:12pt; line-height:var(--line-height,1.6);
               color:#1c1c1c; padding:64px 72px 32px 72px; outline:none; }
  .ql-editor.ql-blank::before { content:none; }
  .ql-editor img { max-width:100%; height:auto; -webkit-user-drag:none; }
  /* An image keeps a life after it lands (v1.19.76): selected wears the
     outline; the corner handles live in #imgui on the body, OUTSIDE the
     contenteditable, so the model never sees the chrome; the browser's own
     image drag is switched off because the sheet does the moving itself. */
  .ql-editor img.az-sel { outline:2px solid #4a90d9; outline-offset:2px; }
  #imgui { position:fixed; display:none; z-index:60; pointer-events:none; }
  #imgui .az-h { position:absolute; width:12px; height:12px; background:#fff;
                 border:2px solid #4a90d9; border-radius:2px; pointer-events:auto; }
  #imgui .az-nw { left:-7px; top:-7px; cursor:nwse-resize; }
  #imgui .az-ne { right:-7px; top:-7px; cursor:nesw-resize; }
  #imgui .az-sw { left:-7px; bottom:-7px; cursor:nesw-resize; }
  #imgui .az-se { right:-7px; bottom:-7px; cursor:nwse-resize; }
  .az-bar { position:fixed; width:0; height:28px; border-left:2px solid #4a90d9;
            display:none; z-index:61; pointer-events:none; }
  /* The inverted page (v1.19.80, retaken v1.19.81): one class on the body -
     the sheet wears black, the text wears white, and the reader's eyes keep
     their night. The DESK joins the night too: the gray surround read as a
     light border around the black page, so it and the shadow go dark with
     everything else. The quote's voice lightens with it; the marks keep
     their blue. */
  body.az-inv { background:#000000; }
  body.az-inv #canvas { background:#000000; box-shadow:none; }
  body.az-inv .ql-editor { color:#ffffff; }
  body.az-inv .ql-editor blockquote { color:#d0d0d0; }
  /* The quote wears italic and bold (v1.19.76): the left bar alone read as
     an indent, not a voice. Declared after the theme's own blockquote rule,
     so the later declaration wins; the thumbnail's raster repeats it below. */
  /* The quote wears italic and bold (v1.19.76) and, since v1.19.77, the
     reader's own quotation marks: the left bar names the block, the two
     Georgia faces quote it. Declared after the theme's own blockquote rule,
     so the later declaration wins; the thumbnail's raster repeats it below. */
  .ql-editor blockquote { border-left: 3px solid #7aa7d8; padding-left: 14px;
               margin: 12px 0; font-style: italic; font-weight: bold; color: #444;
               position: relative; }
  .ql-editor blockquote::before { content: "\201C"; font-family: Georgia, serif;
               font-size: 1.5em; line-height: 0.1em; vertical-align: -0.2em;
               margin-right: 4px; color: #7aa7d8; }
  .ql-editor blockquote::after { content: "\201D"; font-family: Georgia, serif;
               font-size: 1.5em; line-height: 0.1em; vertical-align: -0.2em;
               margin-left: 4px; color: #7aa7d8; }
  /* The journal's typesetting (v1.19.78): the paragraph breathes on the two
     variables the Spacing button turns, the lists pack tight the way Axo
     packed them, the code block wears VS Code's dark face, and the headings
     keep Axo's exact sizes. (Axo's li rides display:flex - a ProseMirror
     shape; Quill's items carry inline content and the marker spans, so the
     packing lands on the margins alone.) Quill 2 renders a code block as a
     ql-code-block-container of div lines, not a pre, so both faces get the
     dark rule; the pre rule stays for anything the clipboard drops in. */
  .ql-editor p { margin: 0 0 var(--p-margin, 0.5em) 0; }
  .ql-editor ul, .ql-editor ol { padding-left: 0 !important; margin-left: 1em !important;
      margin-top: 0 !important; margin-bottom: var(--p-margin, 0.5em) !important; }
  .ql-editor li { margin-top: 0 !important; margin-bottom: -0.1em !important; }
  .ql-editor p + ul, .ql-editor p + ol { margin-top: calc(var(--p-margin, 0.5em) * -0.5) !important; }
  .ql-editor h1 { font-size: 2em; line-height: 1.2; margin: 0.5em 0 0.25em 0; }
  .ql-editor h2 { font-size: 1.5em; line-height: 1.3; margin: 0.5em 0 0.25em 0; }
  .ql-editor pre, .ql-editor .ql-code-block-container { background: #1e1e1e !important;
      color: #d4d4d4 !important; padding: 0.75rem 1rem !important;
      border-radius: 0.5rem !important; margin: 0.5rem 0 !important; }
  .ql-editor pre, .ql-editor .ql-code-block { font-family: 'JetBrains Mono', 'Fira Code', Consolas, monospace !important;
      color: #d4d4d4 !important; }
  .ql-editor .ql-code-block-container { background: #1e1e1e !important; }
  .ql-editor code { background-color: rgba(110,118,129,0.4); padding: 0.2em 0.4em;
      border-radius: 6px; font-size: 85%; font-family: Consolas, 'Courier New', monospace; }
  /* The proofreader's marks and chrome (v1.19.77): a flagged word wears the
     wavy red the eye already knows, the card above it offers the fix or the
     forget, the bubble rides a selection longer than three characters, and
     the note whispers what the document did not need. Every piece of this
     chrome lives on the body, outside the contenteditable, so the document
     model and the saved world see only what the reader wrote. */
  .ql-editor span.ai-err { text-decoration: underline wavy #e53e3e;
               text-decoration-thickness: 2px; text-underline-offset: 3px;
               background: rgba(229,62,62,0.08); cursor: pointer;
               transition: background 0.15s ease; }
  .ql-editor span.ai-err:hover { background: rgba(229,62,62,0.18); }
  #err_pop { position:fixed; display:none; z-index:70; background:#ffffff;
             border:1px solid #c7c7c7; border-radius:6px;
             box-shadow:0 6px 22px rgba(0,0,0,0.35); padding:6px; }
  #err_pop .err-fix { padding:5px 10px; border:1px solid #b7d3b7; background:#f0f9f0;
             color:#1e5c1e; border-radius:4px; cursor:pointer;
             font:12px 'Segoe UI',sans-serif; margin-right:4px; }
  #err_pop .err-fix:hover { background:#dff2df; }
  #err_pop .err-x { padding:5px 9px; border:1px solid #c9c9c9; background:#f4f4f4;
             border-radius:4px; cursor:pointer; font:12px 'Segoe UI',sans-serif; }
  #err_pop .err-x:hover { background:#e8e8e8; }
  #ai_bubble { position:fixed; display:none; z-index:65; background:#2b2f36;
             border-radius:8px; box-shadow:0 6px 22px rgba(0,0,0,0.4); padding:5px; }
  #ai_bubble button { padding:6px 12px; border:none; border-radius:5px;
             background:transparent; color:#e8e8e8; cursor:pointer;
             font:12px 'Segoe UI',sans-serif; }
  #ai_bubble button:hover { background:#3d434d; }
  #ai_menu { position:fixed; display:none; z-index:66; background:#2b2f36;
             border-radius:8px; box-shadow:0 6px 22px rgba(0,0,0,0.4); padding:4px; }
  #ai_menu button { display:block; width:100%; text-align:left; padding:6px 12px;
             border:none; border-radius:5px; background:transparent; color:#e8e8e8;
             cursor:pointer; font:12px 'Segoe UI',sans-serif; white-space:nowrap; }
  #ai_menu button:hover { background:#3d434d; }
  #ai_note { position:fixed; display:none; z-index:71; top:18px; left:50%;
             transform:translateX(-50%); background:#2b2f36; color:#d9e8d9;
             border-radius:6px; padding:8px 16px; font:12px 'Segoe UI',sans-serif;
             box-shadow:0 6px 22px rgba(0,0,0,0.4); }
  .ql-editor a { cursor:pointer; color:#1155cc; text-decoration:underline; }
  sup.fnref { color:#1155cc; cursor:pointer; }
  /* The reader's sub-numbers wear capital letters (v1.19.73): under 1. comes
     A. B. C. and the ladder starts over under every parent - Quill's own
     counter reset per top-level item does the restarting, this recases the
     face. Same selector shape as the engine's own rule, declared after it,
     so the later declaration wins. */
  .ql-editor li[data-list=ordered].ql-indent-1:not(.ql-direction-rtl) > .ql-ui:before { content: counter(list-1, upper-alpha) '. '; }
  sup.fnref::after { content:attr(data-n); }
  sup.fnref.flash { background:#fff3c4; border-radius:2px; }
  /* The footnote list lives under the document, inside the sheet, outside
     Quill's model - the entries are editable on their own recognizance. */
  #fnote { margin:0 72px; padding:0 0 44px 0; }
  #fnote:empty { display:none; }
  #fnote.has { border-top:1px solid #d8d8d8; }
  .fnitem { font-family:'Segoe UI',sans-serif; font-size:10pt; color:#333;
            line-height:1.45; margin:7px 0; outline:none; }
  .fnitem .fnnum { font-weight:bold; color:#1155cc; cursor:pointer; margin-right:7px; }
  .fnitem.flash { background:#fff3c4; border-radius:2px; }
  .fnitem.flash .fnnum { color:#7a5c00; }
  /* The link popover: the ribbon's linkui lands here, near the selection. */
  #linkpop { position:fixed; display:none; z-index:50; background:#ffffff;
             border:1px solid #c7c7c7; border-radius:6px;
             box-shadow:0 6px 22px rgba(0,0,0,0.35); padding:10px; width:300px; }
  #linkpop input { width:100%; box-sizing:border-box; margin:4px 0; padding:6px 8px;
                   border:1px solid #c9c9c9; border-radius:4px;
                   font:12px 'Segoe UI',sans-serif; outline:none; }
  #linkpop input:focus { border-color:#7aa7d8; }
  #linkpop .row { display:flex; gap:6px; margin-top:6px; }
  #linkpop button { flex:1; padding:6px 0; border:1px solid #c9c9c9; border-radius:4px;
                    background:#f4f4f4; cursor:pointer; font:12px 'Segoe UI',sans-serif; }
  #linkpop button:hover { background:#e8e8e8; }
  /* The font dial's thirteen faces - the class attributor's whitelist. */
  .ql-font-segoe-ui { font-family:'Segoe UI',sans-serif; }
  .ql-font-arial { font-family:Arial,sans-serif; }
  .ql-font-calibri { font-family:Calibri,sans-serif; }
  .ql-font-cambria { font-family:Cambria,serif; }
  .ql-font-consolas { font-family:Consolas,monospace; }
  .ql-font-courier-new { font-family:'Courier New',monospace; }
  .ql-font-georgia { font-family:Georgia,serif; }
  .ql-font-impact { font-family:Impact,sans-serif; }
  .ql-font-palatino-linotype { font-family:'Palatino Linotype',serif; }
  .ql-font-tahoma { font-family:Tahoma,sans-serif; }
  .ql-font-times-new-roman { font-family:'Times New Roman',serif; }
  .ql-font-trebuchet-ms { font-family:'Trebuchet MS',sans-serif; }
  .ql-font-verdana { font-family:Verdana,sans-serif; }
</style>
</head>
<body>
<div id="canvas">
  <div id="editor"></div>
  <div id="fnote"></div>
</div>
<div id="linkpop">
  <input id="lp_text" type="text" autocomplete="off">
  <input id="lp_url" type="text" autocomplete="off">
  <div class="row"><button id="lp_ok" type="button"></button><button id="lp_no" type="button"></button></div>
</div>
<script>__QUILL_JS__</script>
<script>
(function(){
'use strict';
function post(o){ try { window.chrome.webview.postMessage(o); } catch(e){} }

// ── fonts & sizes ─────────────────────────────────────────────────────────
var FONTS = ['Segoe UI','Arial','Calibri','Cambria','Consolas','Courier New','Georgia',
             'Impact','Palatino Linotype','Tahoma','Times New Roman','Trebuchet MS','Verdana'];
var LADDER = [8,9,10,11,12,14,16,18,20,24,28,32,36,48,72];
function slug(n){ return String(n).toLowerCase().replace(/\s+/g,'-'); }
function pretty(s){ for (var i=0;i<FONTS.length;i++) if (slug(FONTS[i])===s) return FONTS[i]; return s||''; }

// The size format is a STYLE attributor, not a class one: any point size the
// reader types lands exactly - the ladder is only a stepping stone. The
// engine ships the instance (px-whitelisted for its paste matcher); the
// whitelist opens and the command layer speaks plain points ('13' -> '13pt').
var SizeStyle = Quill.import('attributors/style/size');
SizeStyle.whitelist = null;
Quill.register(SizeStyle, true);
function pt(value){ return /^\d+(\.\d+)?$/.test(String(value)) ? value + 'pt' : value; }

var FontBlot = Quill.import('formats/font');
FontBlot.whitelist = FONTS.map(slug);
Quill.register(FontBlot, true);

// The footnote anchor: an ATOMIC sup embed - the caret can never fall inside
// it, backspace takes the whole anchor, and the visible number rides the
// data-n attribute the renumber pass keeps honest.
var Embed = Quill.import('blots/embed');
class FnRef extends Embed {
  static create(value) {
    var node = super.create(value);
    node.setAttribute('data-fn', String(value));
    return node;
  }
  static value(node) { return node.getAttribute('data-fn') || ''; }
  value() { return { fnref: this.domNode.getAttribute('data-fn') || '' }; }
}
FnRef.blotName = 'fnref';
FnRef.tagName = 'SUP';
FnRef.className = 'fnref';
Quill.register(FnRef);

// ── the editor ────────────────────────────────────────────────────────────
var fnote = document.getElementById('fnote');
var quill = new Quill('#editor', {
  theme: 'snow',
  placeholder: '',
  modules: {
    toolbar: false,
    history: { delay: 400, maxStack: 500, userOnly: true }
  }
});
try { window.__az = quill; } catch(e){}

// Undo and redo answer from the page's own keys as well as the ribbon's.
quill.keyboard.addBinding({ key: 'Z', shortKey: true }, function(){ quill.history.undo(); });
quill.keyboard.addBinding({ key: 'Y', shortKey: true }, function(){ quill.history.redo(); });
quill.keyboard.addBinding({ key: 'Z', shortKey: true, shiftKey: true }, function(){ quill.history.redo(); });

// Select-all answers from the page's own keys too (v1.19.73): the host
// forwards the chord when a ribbon control holds the keyboard, and this
// binding is the sheet's own answer when the chord lands here directly.
quill.keyboard.addBinding({ key: 'A', shortKey: true }, function(){
  quill.setSelection(0, quill.getLength(), 'user');
});

// Axo's chords (v1.19.78): every tool the journal answered from the
// keyboard, the sheet answers too. Quill's stock bindings carry only
// bold, italic and underline, so each chord here owns exactly one law
// and none of them double-fires. Every handler is a toggle - the same
// press that dresses a line undresses it again.
quill.keyboard.addBinding({ key: 'X', shortKey: true, shiftKey: true }, function(){ toggle('strike'); });
quill.keyboard.addBinding({ key: '1', shortKey: true, altKey: true }, function(){ header(1); });
quill.keyboard.addBinding({ key: '2', shortKey: true, altKey: true }, function(){ header(2); });
quill.keyboard.addBinding({ key: '8', shortKey: true, shiftKey: true }, function(){ bullet(); });
quill.keyboard.addBinding({ key: '7', shortKey: true, shiftKey: true }, function(){ number(); });
quill.keyboard.addBinding({ key: 'B', shortKey: true, shiftKey: true }, function(){ quote(); });
quill.keyboard.addBinding({ key: 'C', shortKey: true, altKey: true }, function(){ toggle('code-block'); });
quill.keyboard.addBinding({ key: 'E', shortKey: true }, function(){ toggle('code'); });
quill.keyboard.addBinding({ key: 'L', shortKey: true, shiftKey: true }, function(){ align('left'); });
quill.keyboard.addBinding({ key: 'E', shortKey: true, shiftKey: true }, function(){ align('center'); });
quill.keyboard.addBinding({ key: 'R', shortKey: true, shiftKey: true }, function(){ align('right'); });

// ── footnotes ─────────────────────────────────────────────────────────────
function nextFnId(){
  var max = 0, m, i;
  var pool = quill.root.querySelectorAll('sup.fnref[data-fn]');
  for (i=0;i<pool.length;i++){ m = /fn(\d+)/.exec(pool[i].getAttribute('data-fn')||''); if (m) max = Math.max(max, +m[1]); }
  pool = fnote.querySelectorAll('.fnitem[data-fn]');
  for (i=0;i<pool.length;i++){ m = /fn(\d+)/.exec(pool[i].getAttribute('data-fn')||''); if (m) max = Math.max(max, +m[1]); }
  return 'fn' + (max + 1);
}

// One law for the whole list: anchors renumber by document order, every
// anchor owns exactly one entry, entries follow anchor order, orphans die.
function renumber(){
  var refs = quill.root.querySelectorAll('sup.fnref');
  var seen = [], i, id;
  for (i=0;i<refs.length;i++){
    id = refs[i].getAttribute('data-fn') || ('fn' + (i+1));
    refs[i].setAttribute('data-n', String(i+1));
    seen.push(id);
    var it = fnote.querySelector('.fnitem[data-fn="' + id + '"]');
    if (!it){
      it = document.createElement('div');
      it.className = 'fnitem';
      it.setAttribute('data-fn', id);
      it.setAttribute('contenteditable', 'true');
      var num = document.createElement('span'); num.className = 'fnnum'; num.setAttribute('contenteditable','false');
      var txt = document.createElement('span'); txt.className = 'fntxt';
      it.appendChild(num); it.appendChild(txt);
    }
    var n = it.querySelector('.fnnum'); if (n) n.textContent = (i+1) + '.';
    fnote.appendChild(it);   // an appendChild of a live child is a move
  }
  var items = fnote.querySelectorAll('.fnitem');
  for (var j=items.length-1;j>=0;j--)
    if (seen.indexOf(items[j].getAttribute('data-fn')) < 0) items[j].parentNode.removeChild(items[j]);
  fnote.className = refs.length ? 'has' : '';
}

function insertFootnote(){
  var sel = quill.getSelection(true);
  if (!sel) return;
  var id = nextFnId();
  quill.insertEmbed(sel.index, 'fnref', id, 'user');
  quill.setSelection(sel.index + 1);
  renumber();
}

function jumpToEntry(id){
  var it = fnote.querySelector('.fnitem[data-fn="' + id + '"]');
  if (it){ it.scrollIntoView({ behavior:'smooth', block:'center' }); flash(it); }
}
function jumpToRef(id){
  var sup = quill.root.querySelector('sup.fnref[data-fn="' + id + '"]');
  if (sup){ sup.scrollIntoView({ behavior:'smooth', block:'center' }); flash(sup); }
}
function flash(el){ el.classList.add('flash'); setTimeout(function(){ el.classList.remove('flash'); }, 900); }

quill.root.addEventListener('click', function(e){
  var t = e.target;
  if (t && t.closest){
    var sup = t.closest('sup.fnref');
    if (sup){ e.preventDefault(); jumpToEntry(sup.getAttribute('data-fn')); return; }
    var a = t.closest('a[href]');
    if (a){ e.preventDefault(); post({ type:'link', url: a.getAttribute('href') || '' }); }
  }
});
fnote.addEventListener('click', function(e){
  var t = e.target;
  if (t && t.classList && t.classList.contains('fnnum')){
    var it = t.closest ? t.closest('.fnitem') : null;
    if (it) jumpToRef(it.getAttribute('data-fn'));
  }
});

// ── the link popover ──────────────────────────────────────────────────────
var lpop = document.getElementById('linkpop'),
    lpText = document.getElementById('lp_text'),
    lpUrl = document.getElementById('lp_url'),
    lpOk = document.getElementById('lp_ok'),
    lpNo = document.getElementById('lp_no');
var i18n = { apply:'Apply', remove:'Remove', linkUrl:'Link URL' };

function closeLinkPop(){ lpop.style.display = 'none'; }
function openLinkPop(){
  var sel = quill.getSelection();
  var hasSel = !!(sel && sel.length > 0);
  var f = quill.getFormat();
  lpText.style.display = hasSel ? 'none' : 'block';
  lpText.value = '';
  lpUrl.value = hasSel && f.link ? String(f.link) : '';
  lpOk.textContent = i18n.apply;
  lpNo.textContent = i18n.remove;
  lpUrl.placeholder = i18n.linkUrl;
  lpop.style.display = 'block';
  var rect = null;
  try {
    var dsel = document.getSelection();
    if (dsel && dsel.rangeCount && dsel.anchorNode && quill.root.contains(dsel.anchorNode))
      rect = dsel.getRangeAt(0).getBoundingClientRect();
  } catch(e){}
  var x, y;
  if (rect && (rect.top || rect.bottom)){ x = rect.left; y = rect.bottom + 8; }
  else { x = (window.innerWidth - 320) / 2; y = 120; }
  x = Math.max(8, Math.min(x, window.innerWidth - 320));
  y = Math.max(8, y);
  lpop.style.left = x + 'px';
  lpop.style.top = y + 'px';
  lpUrl.focus();
}
function applyLink(){
  var url = lpUrl.value.trim();
  if (!url){ closeLinkPop(); return; }
  var sel = quill.getSelection(true);
  if (sel && sel.length > 0){
    quill.formatText(sel.index, sel.length, 'link', url, 'user');
  } else {
    var txt = lpText.value.trim() || url;
    quill.insertText(sel.index, txt, 'link', url, 'user');
  }
  closeLinkPop();
  reportState();
}
function removeLink(){
  var sel = quill.getSelection(true);
  if (sel && sel.length > 0){
    if (quill.getFormat().link) quill.formatText(sel.index, sel.length, 'link', false, 'user');
  } else {
    var el = caretEl();
    var a = el && el.closest ? el.closest('a[href]') : null;
    if (a){
      var blot = Quill.find(a);
      if (blot){ var at = quill.getIndex(blot); quill.formatText(at, blot.length(), 'link', false, 'user'); }
    }
  }
  closeLinkPop();
  reportState();
}
lpOk.addEventListener('click', applyLink);
lpNo.addEventListener('click', removeLink);
lpUrl.addEventListener('keydown', function(e){
  if (e.key === 'Enter'){ e.preventDefault(); applyLink(); }
  else if (e.key === 'Escape'){ e.preventDefault(); closeLinkPop(); }
});
lpText.addEventListener('keydown', function(e){
  if (e.key === 'Enter'){ e.preventDefault(); lpUrl.focus(); }
  else if (e.key === 'Escape'){ e.preventDefault(); closeLinkPop(); }
});
// A click inside the text moves the caret; a popover holding a stale
// selection would then dress the wrong words. Close it first.
quill.root.addEventListener('mousedown', function(){ if (lpop.style.display === 'block') closeLinkPop(); });

// ── images ────────────────────────────────────────────────────────────────
function insertImage(src){
  if (!src) return;
  var sel = quill.getSelection(true);
  var at = sel ? sel.index : quill.getLength();
  quill.insertEmbed(at, 'image', src, 'user');
  // A fresh picture arrives already selected (v1.19.76): the handles
  // introduce themselves and Delete works before the first click.
  try {
    var leaf = quill.getLeaf(at);
    if (leaf && leaf[0] && leaf[0].domNode && leaf[0].domNode.tagName === 'IMG')
      selectImage(leaf[0].domNode);
  } catch(e){}
}
function readImages(files){
  for (var i=0;i<files.length;i++){
    if (/^image\//.test(files[i].type || '')){
      (function(file){
        var r = new FileReader();
        r.onload = function(){ insertImage(String(r.result)); };
        r.readAsDataURL(file);
      })(files[i]);
    }
  }
}
quill.root.addEventListener('paste', function(e){
  var files = e.clipboardData && e.clipboardData.files;
  if (files && files.length){
    for (var i=0;i<files.length;i++)
      if (/^image\//.test(files[i].type || '')){ e.preventDefault(); break; }
    readImages(files);
  }
});
quill.root.addEventListener('drop', function(e){
  var files = e.dataTransfer && e.dataTransfer.files;
  if (files && files.length){
    var has = false;
    for (var i=0;i<files.length;i++) if (/^image\//.test(files[i].type || '')) has = true;
    if (has){ e.preventDefault(); e.stopPropagation(); readImages(files); }
  }
});

// ── an image's life after insert (v1.19.76) ───────────────────────────────
// Click a picture and it is selected: an outline plus four corner handles on
// the body, outside Quill's contenteditable. Drag the picture and it moves,
// a drop bar riding the caret under the mouse; pull a corner and it resizes
// with the aspect held (height stays auto, width rides the blot's own width
// attribute, so the Delta carries every new size home through save and
// load); Delete or Backspace takes it away; Escape lets go; a click anywhere
// else lets go. The width commit writes old then new through formatText, so
// undo reads one step, not ten, and the browser's own image drag is off.
var imgui = null, imgBar = null, imgHandles = [], selImg = null, imgDrag = null;

function ensureImgUI(){
  if (imgui) return;
  imgui = document.createElement('div');
  imgui.id = 'imgui';
  var corners = ['nw','ne','sw','se'], i;
  for (i=0;i<corners.length;i++){
    var h = document.createElement('div');
    h.className = 'az-h az-' + corners[i];
    h.setAttribute('data-dir', corners[i]);
    h.addEventListener('mousedown', startImgResize);
    imgui.appendChild(h);
    imgHandles.push(h);
  }
  imgBar = document.createElement('div');
  imgBar.className = 'az-bar';
  document.body.appendChild(imgui);
  document.body.appendChild(imgBar);
}

function positionImgUI(){
  if (selImg && !selImg.isConnected){ deselectImage(); return; }
  if (!selImg || !imgui) return;
  var r = selImg.getBoundingClientRect();
  imgui.style.display = 'block';
  imgui.style.left = r.left + 'px';
  imgui.style.top = r.top + 'px';
  imgui.style.width = r.width + 'px';
  imgui.style.height = r.height + 'px';
}

function selectImage(img){
  if (!img || !img.isConnected) return;
  ensureImgUI();
  if (selImg && selImg !== img) selImg.classList.remove('az-sel');
  selImg = img;
  img.classList.add('az-sel');
  positionImgUI();
}

function deselectImage(){
  if (selImg) selImg.classList.remove('az-sel');
  selImg = null;
  if (imgui) imgui.style.display = 'none';
  if (imgBar) imgBar.style.display = 'none';
}

function imgIndex(){
  if (!selImg || !selImg.isConnected) return null;
  try { var blot = Quill.find(selImg); return blot ? quill.getIndex(blot) : null; }
  catch(e){ return null; }
}

function imgWidthPx(){
  if (!selImg) return 0;
  var w = parseInt(selImg.getAttribute('width'), 10);
  if (w > 0) return w;
  return Math.round(parseFloat(getComputedStyle(selImg).width)) || 0;
}

function deleteSelectedImage(){
  var at = imgIndex();
  if (at === null) return;
  quill.deleteText(at, 1, 'user');
  deselectImage();
  reportState();
}

// Where the mouse points in the MODEL: the native caret goes under the
// cursor and Quill maps the selection back to an index.
// One point, one range: the standard caretPositionFromPoint first, the
// webkit legacy second - a runtime that dropped either still drops the bar.
function rangeAtPoint(x, y){
  try {
    if (document.caretPositionFromPoint){
      var p = document.caretPositionFromPoint(x, y);
      if (p && p.offsetNode){
        var r = document.createRange();
        r.setStart(p.offsetNode, p.offset);
        r.collapse(true);
        return r;
      }
      return null;
    }
  } catch(e){}
  try { return document.caretRangeFromPoint(x, y); } catch(e2){ return null; }
}
// The block under the point, read straight from the DOM - the quiet
// fallback when Quill's own selection mapping stays asleep.
function blockIndexAtPoint(x, y){
  try {
    var range = rangeAtPoint(x, y);
    if (!range || !range.startContainer) return null;
    var n = range.startContainer.nodeType === 3 ? range.startContainer.parentElement : range.startContainer;
    while (n && quill.root.contains(n)){
      var blot = Quill.find(n, true);
      if (blot) return quill.getIndex(blot);
      n = n.parentElement;
    }
  } catch(e){ return null; }
  return null;
}
function indexAtPoint(x, y){
  try {
    var range = rangeAtPoint(x, y);
    if (!range || !range.startContainer || !quill.root.contains(range.startContainer)) return null;
    var s = window.getSelection();
    s.removeAllRanges();
    s.addRange(range);
    // v1.19.81: the mapping reads the native selection THIS INSTANT - the
    // selectionchange event rides its own task, so quill.getSelection()'s
    // last range was still the picture's own seat and every drop died as
    // "no move". getRange() maps synchronously; update() is the backup.
    var b = null;
    try { var gr = quill.selection.getRange(); if (gr && gr[0] && typeof gr[0].index === 'number') b = gr[0]; } catch(e0){}
    if (!b){ try { quill.selection.update(); } catch(e1){} b = quill.getSelection(); }
    if (b && typeof b.index === 'number') return b.index;
  } catch(e){}
  return blockIndexAtPoint(x, y);   // the silent fallback (v1.19.80)
}

// The nearest block's edge, read straight from the geometry (v1.19.81):
// a release over blank paper where no caret lives still lands - the
// closest block's before or after, decided by the point's height.
function nearestBlockIndex(x, y){
  try {
    var blocks = quill.root.querySelectorAll('p, h1, h2, h3, ol, ul, blockquote, pre, img');
    var best = null, bestD = Infinity;
    for (var i = 0; i < blocks.length; i++){
      var r = blocks[i].getBoundingClientRect();
      if (!r || (!r.height && !r.width)) continue;
      var d = (y >= r.top && y <= r.bottom) ? 0 : Math.min(Math.abs(y - r.top), Math.abs(y - r.bottom));
      if (d < bestD){ bestD = d; best = blocks[i]; }
    }
    if (!best) return null;
    var blot = Quill.find(best, true) || Quill.find(best);
    if (!blot) return null;
    var at = quill.getIndex(blot);
    var r2 = best.getBoundingClientRect();
    var len = 1; try { len = blot.length() || 1; } catch(e2){}
    return (y > r2.top + r2.height / 2) ? at + len : at;
  } catch(e){ return null; }
}

function placeDropBar(x, y){
  var r = null;
  try {
    var range = rangeAtPoint(x, y);
    if (range){
      var rects = range.getClientRects();
      if (rects.length) r = rects[0];
      else if (range.startContainer.getBoundingClientRect) r = range.startContainer.getBoundingClientRect();
    }
  } catch(e){}
  if (!r || (!r.width && !r.height)){ if (imgBar) imgBar.style.display = 'none'; return; }
  imgBar.style.display = 'block';
  imgBar.style.left = Math.max(0, r.left - 1) + 'px';
  imgBar.style.top = r.top + 'px';
}

function startImgResize(e){
  if (!selImg) return;
  e.preventDefault(); e.stopPropagation();
  imgDrag = { mode:'size', x:e.clientX, y:e.clientY,
              origW: imgWidthPx(), curW: 0,
              dir: (e.currentTarget && e.currentTarget.getAttribute('data-dir')) || 'se' };
}

document.addEventListener('mousedown', function(e){
  var t = e.target;
  if (t && t.closest && t.closest('#imgui')) return;          // the handles
  if (t && t.tagName === 'IMG' && quill.root.contains(t)){
    try { quill.root.focus({ preventScroll:true }); } catch(err){}
    if (selImg !== t) selectImage(t); else positionImgUI();
    imgDrag = { mode:'move', x:e.clientX, y:e.clientY, moved:false };
    e.preventDefault();   // no native image drag, no caret torn from the page
    return;
  }
  if (selImg) deselectImage();
}, true);

document.addEventListener('mousemove', function(e){
  if (!imgDrag) return;
  var dx = e.clientX - imgDrag.x, dy = e.clientY - imgDrag.y;
  if (imgDrag.mode === 'size'){
    var w = imgDrag.origW + ((imgDrag.dir === 'ne' || imgDrag.dir === 'se') ? dx : -dx);
    imgDrag.curW = Math.max(64, Math.min(672, w));
    if (selImg) selImg.setAttribute('width', String(Math.round(imgDrag.curW)));
    positionImgUI();
    return;
  }
  if (!imgDrag.moved && Math.abs(dx) < 4 && Math.abs(dy) < 4) return;
  if (!imgDrag.moved){
    imgDrag.moved = true;
    if (imgui) imgui.style.display = 'none';
    document.body.style.cursor = 'grabbing';
    if (selImg){ selImg.style.opacity = '0.45'; selImg.style.pointerEvents = 'none'; }   // the caret sees through the traveler (v1.19.81)
  }
  placeDropBar(e.clientX, e.clientY);
}, true);

document.addEventListener('mouseup', function(e){
  if (!imgDrag) return;
  var d = imgDrag;
  imgDrag = null;
  if (selImg) selImg.style.opacity = '';   // the ghost goes home, whatever the ending
  document.body.style.cursor = '';
  if (imgBar) imgBar.style.display = 'none';
  if (d.mode === 'size'){
    if (selImg && d.curW > 0){
      var wNew = Math.round(d.curW), wOld = Math.round(d.origW), at = imgIndex();
      if (at !== null && wNew !== wOld){
        quill.formatText(at, 1, 'width', String(wOld), 'silent');
        quill.formatText(at, 1, 'width', String(wNew), 'user');
      }
    }
    if (selImg) positionImgUI();
    reportState();
    return;
  }
  if (!d.moved) return;
  var to = indexAtPoint(e.clientX, e.clientY);
  if (to === null) to = nearestBlockIndex(e.clientX, e.clientY);   // blank paper lands too (v1.19.81)
  if (to !== null) to = Math.max(0, Math.min(to, quill.getLength()));
  if (selImg) selImg.style.pointerEvents = '';   // solid again, wherever it landed
  var from = imgIndex();
  if (to === null || from === null || to === from || to === from + 1){
    if (selImg) positionImgUI();
    return;
  }
  var src = selImg.getAttribute('src') || '';
  var wpx = imgWidthPx();
  quill.deleteText(from, 1, 'user');
  var dest = to > from ? to - 1 : to;
  quill.insertEmbed(dest, 'image', src, 'user');
  if (wpx > 0) quill.formatText(dest, 1, 'width', String(wpx), 'user');
  try {
    var leaf = quill.getLeaf(dest);
    if (leaf && leaf[0] && leaf[0].domNode && leaf[0].domNode.tagName === 'IMG')
      selectImage(leaf[0].domNode);
  } catch(err){}
  reportState();
}, true);

// The sheet's own keys: Delete and Backspace take the selected picture
// (unless real text is selected - that deletion keeps its own laws);
// Escape lets go. Capture on the document, above Quill's bindings.
document.addEventListener('keydown', function(e){
  if (!selImg) return;
  if (e.key === 'Delete' || e.key === 'Backspace'){
    var s = quill.getSelection();
    if (s && s.length > 0) return;
    e.preventDefault(); e.stopPropagation();
    deleteSelectedImage();
  } else if (e.key === 'Escape'){
    deselectImage();
  }
}, true);

// The browser's own picture drag would fight the sheet's move; it is off.
quill.root.addEventListener('dragstart', function(e){
  if (e.target && e.target.tagName === 'IMG') e.preventDefault();
});

window.addEventListener('scroll', function(){ if (selImg && !imgDrag) positionImgUI(); }, true);
window.addEventListener('resize', function(){ if (selImg && !imgDrag) positionImgUI(); });

// Text chosen with the mouse outranks the picture: the chrome steps aside.
quill.on('selection-change', function(range){
  if (range && range.length > 0 && selImg) deselectImage();
});

// ── state, save, title ────────────────────────────────────────────────────
function caretEl(){
  try {
    var s = quill.getSelection();
    if (!s) return null;
    var leaf = quill.getLeaf(s.index);
    if (leaf && leaf[0] && leaf[0].domNode){
      var n = leaf[0].domNode;
      return n.nodeType === 1 ? n : n.parentElement;
    }
  } catch(e){}
  return null;
}
function effFont(){
  var f = quill.getFormat().font;
  if (f) return pretty(String(f));
  var el = caretEl();
  if (el){
    var fam = (getComputedStyle(el).fontFamily || '').toLowerCase();
    for (var i=0;i<FONTS.length;i++) if (fam.indexOf(FONTS[i].toLowerCase())>=0) return FONTS[i];
  }
  return 'Times New Roman';
}
function effSize(){
  var f = quill.getFormat().size;
  if (f){ var m = parseFloat(String(f)); if (m) return String(Math.round(m)); }
  var el = caretEl();
  if (el){ var px = parseFloat(getComputedStyle(el).fontSize) || 16; return String(Math.round(px * 0.75)); }
  return '12';
}
function reportState(){
  var sel = quill.getSelection();
  if (!sel) return;
  var f = quill.getFormat();
  // The block layer rides along (v1.19.73): which header the line wears,
  // what list it stands in, how deep it is nested, whether it is a quote -
  // the ribbon's toggle lights read these and nothing else.
  post({ type:'state', edit:true, font: effFont(), size: effSize(),
         b: !!f.bold, i: !!f.italic, u: !!f.underline, s: !!f.strike,
         h: f.header || 0, list: f.list || '', sub: f.indent || 0,
         quote: !!f.blockquote, page: 1 });
}
quill.on('selection-change', function(){ reportState(); });

function docTitle(){
  var lines = quill.getText().split('\n');
  for (var i=0;i<lines.length;i++){
    var s = lines[i].replace(/\s+/g,' ').trim();
    if (s) return s.length > 40 ? s.slice(0,40) + '...' : s;
  }
  return '';
}
// The save payload carries the document THREE ways: the model's own Delta
// (the exact round-trip - every font, size, anchor and link comes home),
// the rendered HTML (thumbnails, debugging, other tools), and the footnote
// list. A world without a Delta section - a legacy session from the old
// pages world - falls back to the clipboard converter and keeps its text.
function docHtml(){
  var delta = stripFlagsFromDelta();
  return '<section data-az="doc">' + aiStripHtml(quill.root.innerHTML) + '</section>' +
         '<section data-az="fn">' + fnote.innerHTML + '</section>' +
         '<section data-az="delta">' + delta + '</section>';
}
var saveTimer = null, thumbTimer = null, fnTimer = null, worldSeq = 0;
function saveNow(){
  if (saveTimer){ clearTimeout(saveTimer); saveTimer = null; }
  post({ type:'save', html: docHtml(), seq: worldSeq, title: docTitle() });
}
function scheduleSave(){
  if (saveTimer) clearTimeout(saveTimer);
  saveTimer = setTimeout(saveNow, 700);
}
quill.on('text-change', function(){
  scheduleSave();
  if (fnTimer) clearTimeout(fnTimer);
  fnTimer = setTimeout(renumber, 120);
  if (thumbTimer) clearTimeout(thumbTimer);
  thumbTimer = setTimeout(postThumbs, 900);
});

// ── load: our own two-section format, or the legacy world's pages ─────────
function loadWorld(html, seq){
  worldSeq = seq|0;
  var docPart = String(html || ''), fnPart = '', deltaPart = '';
  if (docPart.indexOf('data-az="doc"') >= 0){
    var t = document.createElement('div');
    t.innerHTML = docPart;
    var d = t.querySelector('section[data-az="doc"]');
    var f = t.querySelector('section[data-az="fn"]');
    var dz = t.querySelector('section[data-az="delta"]');
    if (d) docPart = d.innerHTML;
    if (f) fnPart = f.innerHTML;
    if (dz) deltaPart = dz.textContent;
  }
  fnote.innerHTML = fnPart;
  var restored = false;
  if (deltaPart){
    try { quill.setContents(JSON.parse(deltaPart), 'silent'); restored = true; } catch(e){}
  }
  if (!restored){
    try {
      quill.deleteText(0, quill.getLength(), 'silent');
      if (docPart) quill.clipboard.dangerouslyPasteHTML(0, docPart, 'silent');
    } catch(e){ try { quill.setText('', 'silent'); } catch(e2){} }
  }
  quill.history.clear();
  aiReset();   // a new world starts clean: no flags, no remembered hash,
               // no popup chrome and no scan left over from the old one
  renumber();
  try { window.scrollTo(0, 0); } catch(e){}
  post({ type:'pages', count: 1 });
  post({ type:'title', seq: worldSeq, title: docTitle() });
  postThumbs();
}

// ── the page's raster for the sidebar's rail ──────────────────────────────
// One sheet, one picture. The SVG foreignObject paints the real document
// (fonts, sizes, links, images) into a Letter-sized frame; a refusal
// anywhere degrades to the empty thumb the rail already knows how to wear.
var THUMB_CSS = '.doc{width:816px;height:1056px;background:#fff;overflow:hidden;position:relative;' +
  "font-family:'Times New Roman',serif;font-size:12pt;line-height:1.5;color:#1c1c1c;}" +
  '.docbody{padding:64px 72px 0 72px;}' +
  '.docbody p,.docbody ol,.docbody ul,.docbody pre,.docbody blockquote,.docbody h1,.docbody h2,.docbody h3,.docbody h4,.docbody h5,.docbody h6{margin:0;padding:0}' +
  '.docbody img{max-width:100%;height:auto;}' +
  '.docbody blockquote{border-left:3px solid #7aa7d8;padding-left:14px;margin:12px 0;' +
  'font-style:italic;font-weight:bold;color:#444;position:relative;}' +
  '.docbody blockquote:before{content:"\\201C";font-family:Georgia,serif;font-size:1.5em;' +
  'line-height:0.1em;vertical-align:-0.2em;margin-right:4px;color:#7aa7d8;}' +
  '.docbody blockquote:after{content:"\\201D";font-family:Georgia,serif;font-size:1.5em;' +
  'line-height:0.1em;vertical-align:-0.2em;margin-left:4px;color:#7aa7d8;}' +
  '.docbody a{color:#1155cc;text-decoration:underline;}' +
  'sup.fnref{color:#1155cc;}sup.fnref::after{content:attr(data-n);}' +
  ".docfn{margin:0 72px;padding:12px 0 0 0;border-top:1px solid #d8d8d8;font-family:'Segoe UI',sans-serif;font-size:10pt;color:#333;}" +
  '.docfn .fnnum{font-weight:bold;color:#1155cc;margin-right:7px;}';
function postThumbs(){
  try {
    // Chromium's SVG-as-image refuses subresource loads - even data: URIs -
    // so the raster wears a neutral block where an image stood; every other
    // mark (fonts, sizes, links, footnotes) paints for real.
    var docBody = aiStripHtml(quill.root.innerHTML).replace(/<img\b([^>]*)>/gi, function(m, attrs){
      // A resized picture keeps its width in the raster (v1.19.76): the
      // width attribute (the model's own carrier) or an inline style wins,
      // everything else wears the neutral block.
      var a = String(attrs || ''),
          m1 = /(?:^|\s)width="(\d+)"/i.exec(a),
          m2 = /width:\s*(\d+(?:\.\d+)?)px/i.exec(a),
          px = m1 ? parseFloat(m1[1]) : (m2 ? parseFloat(m2[1]) : 0),
          w = px > 0 ? Math.max(24, Math.min(672, Math.round(px))) : 88;
      return '<span style="display:inline-block;width:' + w + 'px;height:22px;background:#e4e4e4;border-radius:2px;vertical-align:middle;"></span>';
    });
    var fnBody = fnote.innerHTML.replace(/<img\b[^>]*>/gi, '');
    var svg = '<svg xmlns="http://www.w3.org/2000/svg" width="816" height="1056">' +
      '<foreignObject width="100%" height="100%">' +
      '<div xmlns="http://www.w3.org/1999/xhtml" class="doc">' +
      '<style>' + THUMB_CSS + '</style>' +
      '<div class="docbody">' + docBody + '</div>' +
      (fnBody ? '<div class="docfn">' + fnBody + '</div>' : '') +
      '</div></foreignObject></svg>';
    var img = new Image();
    img.onload = function(){
      try {
        var c = document.createElement('canvas');
        c.width = 408; c.height = 528;
        var ctx = c.getContext('2d');
        ctx.fillStyle = '#ffffff';
        ctx.fillRect(0, 0, c.width, c.height);
        ctx.scale(0.5, 0.5);
        ctx.drawImage(img, 0, 0, 816, 1056);
        post({ type:'thumbs', seq: worldSeq, thumbs: [c.toDataURL('image/png')] });
      } catch(e){ post({ type:'thumbs', seq: worldSeq, thumbs: [''] }); }
    };
    img.onerror = function(){ post({ type:'thumbs', seq: worldSeq, thumbs: [''] }); };
    img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
  } catch(e){ post({ type:'thumbs', seq: worldSeq, thumbs: [''] }); }
}

// ── the editor's second brain (v1.19.77) ──────────────────────────────────
// Two doors, both opened here, both answered by the host's own dial. The
// quiet door: five seconds after the typing stops, ONE batched scan of the
// whole document goes out - never a keystroke, an untouched document never
// asks twice - and the answer dresses every stumble in wavy red at once.
// The loud door: a selection longer than three characters raises the
// bubble, and only a button click ever bills a rewrite. The flags ride the
// model as a silent format so minor edits around them keep them, and the
// save, the load and the thumbnail all strip them again: they are chrome
// for the reading eye, not content.

var Delta = Quill.import('delta');
var InlineBase = Quill.import('blots/inline');
class AiErrBlot extends InlineBase {}
AiErrBlot.blotName = 'ai-err';
AiErrBlot.tagName = 'SPAN';
AiErrBlot.className = 'ai-err';
Quill.register(AiErrBlot);

var SCAN_IDLE_DELAY_MS = 5000;
var scanTimer = null, isDirty = false, lastScannedHash = '', scanInFlight = false;
var ignoredWords = new Set();
var activeFlags = [];      // { index, length, word, suggestion }
var errPop = null, aiBubble = null, aiMenu = null, aiNote = null;
var aiNoteTimer = null, aiBubbleRect = null, aiInFlight = false, aiPending = null;

function hashString(s){
  var h = 5381, i;
  for (i=0;i<s.length;i++){ h = ((h << 5) + h + s.charCodeAt(i)) | 0; }
  return String(h < 0 ? -h : h);
}

function aiStripHtml(html){
  return String(html || '')
    .replace(/ class="ai-err"/g, '')
    .replace(/ data-sug="[^"]*"/g, '');
}

// The proofreader's marks are stripped from the Delta before the world is
// saved: an attribute walk over the ops that drops every ai-err and keeps
// everything else - font, size, width, link, footnote - exactly as it was.
function stripFlagsFromDelta(){
  try {
    var ops = quill.getContents().ops || [], clean = [], i, k;
    for (i=0;i<ops.length;i++){
      var op = ops[i];
      if (op.attributes && op.attributes['ai-err'] !== undefined){
        var a = {}, c = {};
        for (k in op.attributes) if (k !== 'ai-err') a[k] = op.attributes[k];
        if (op.insert !== undefined) c.insert = op.insert;
        if (op.retain !== undefined) c.retain = op.retain;
        if (op.delete !== undefined) c.delete = op.delete;
        for (k in a){ if (!c.attributes) c.attributes = {}; c.attributes[k] = a[k]; }
        clean.push(c);
      } else clean.push(op);
    }
    return JSON.stringify({ ops: clean }).replace(/</g, '\\u003c');
  } catch(e){
    return JSON.stringify(quill.getContents()).replace(/</g, '\\u003c');
  }
}

function aiReset(){
  isDirty = false;
  lastScannedHash = '';
  scanInFlight = false;
  aiInFlight = false;
  aiPending = null;
  if (scanTimer){ clearTimeout(scanTimer); scanTimer = null; }
  clearGrammarFlags();
  hideErrPop(); hideAiBubble(); hideAiNote();
}

// ── the quiet door: the idle batch scan ───────────────────────────────────
// Typing resets the timer every keystroke; only five full seconds of
// stillness open the door, and the document's hash keeps an untouched or
// already-scanned text from asking again.
quill.on('text-change', function(delta, oldDelta, source){
  if (source !== 'user') return;
  isDirty = true;
  if (scanTimer) clearTimeout(scanTimer);
  scanTimer = setTimeout(triggerTimerScan, SCAN_IDLE_DELAY_MS);
});

function triggerTimerScan(){
  if (!isDirty) return;
  isDirty = false;
  var fullText = quill.getText().trim();
  if (fullText.length < 5) return;
  fullText = fullText.slice(0, 24000);
  var hash = hashString(fullText);
  if (hash === lastScannedHash) return;
  if (scanInFlight){ scanTimer = setTimeout(triggerTimerScan, SCAN_IDLE_DELAY_MS); return; }
  lastScannedHash = hash;
  scanInFlight = true;
  post({ type:'ai_timer_grammar_scan', text: fullText, ignored: Array.from(ignoredWords), seq: worldSeq });
}

function clearGrammarFlags(){
  var spans = [];
  try { spans = Array.prototype.slice.call(quill.root.querySelectorAll('span.ai-err')); } catch(e){}
  for (var i=0;i<spans.length;i++){
    try {
      var blot = Quill.find(spans[i]);
      if (!blot) continue;
      var at = quill.getIndex(blot), len = blot.length();
      if (len > 0) quill.formatText(at, len, 'ai-err', false, 'silent');
    } catch(e){}
  }
  activeFlags = [];
}

function escRe(s){ return String(s).replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }

// One answer dresses the whole document at once: every flagged word that is
// still in the text (and not ignored) gets its silent format, the flags
// list remembers where each one landed, and the counts keep a runaway
// model from painting the page red.
function applyScanResult(msg){
  scanInFlight = false;
  var seq = (msg && typeof msg.seq === 'number') ? msg.seq|0 : worldSeq;
  if (seq !== worldSeq) return;   // a stale world's answer dresses nobody
  var errors = (msg && msg.errors) || [];
  if (!errors.length) return;
  clearGrammarFlags();
  var text = quill.getText(), dressed = 0;
  for (var i=0;i<errors.length && dressed < 300;i++){
    var word = String((errors[i] && errors[i].word) || '').trim();
    var sug = String((errors[i] && errors[i].suggestion) || '').trim();
    if (!word || word.length > 60 || !sug) continue;
    if (ignoredWords.has(word.toLowerCase())) continue;
    var re;
    try { re = new RegExp('\\b' + escRe(word) + '\\b', 'gi'); } catch(e){ continue; }
    var m, hits = 0;
    while ((m = re.exec(text)) && hits < 40 && dressed < 300){
      quill.formatText(m.index, m[0].length, 'ai-err', true, 'silent');
      activeFlags.push({ index: m.index, length: m[0].length, word: word, suggestion: sug });
      dressed++; hits++;
    }
  }
}

// ── the red word's little card ────────────────────────────────────────────
function ensureErrPop(){
  if (errPop) return;
  errPop = document.createElement('div');
  errPop.id = 'err_pop';
  var fix = document.createElement('button');
  fix.className = 'err-fix'; fix.type = 'button';
  var no = document.createElement('button');
  no.className = 'err-x'; no.type = 'button';
  no.textContent = '\u2715'; no.title = 'Ignore';
  fix.addEventListener('click', function(){ fixErrWord(); });
  no.addEventListener('click', function(){ ignoreErrWord(); });
  errPop.appendChild(fix); errPop.appendChild(no);
  document.body.appendChild(errPop);
}

function openErrPop(span){
  ensureErrPop();
  var at = null, len = 0;
  try {
    var blot = Quill.find(span);
    if (blot){ at = quill.getIndex(blot); len = blot.length(); }
  } catch(e){}
  var word = String(span.textContent || '').trim();
  var flag = null, i;
  for (i=0;i<activeFlags.length;i++)
    if (at !== null && activeFlags[i].index === at && activeFlags[i].length === len){ flag = activeFlags[i]; break; }
  if (!flag)
    for (i=0;i<activeFlags.length;i++)
      if (activeFlags[i].length === word.length && activeFlags[i].word.toLowerCase() === word.toLowerCase()){ flag = activeFlags[i]; break; }
  errPop.setAttribute('data-word', word);
  errPop.setAttribute('data-sug', flag ? flag.suggestion : '');
  errPop.setAttribute('data-at', at === null ? '' : String(at));
  errPop.setAttribute('data-len', String(len));
  var fixBtn = errPop.querySelector('.err-fix');
  fixBtn.textContent = flag ? flag.suggestion : word;
  fixBtn.style.display = flag ? 'inline-block' : 'none';
  errPop.style.display = 'block';
  var r = span.getBoundingClientRect();
  var top = r.top - errPop.offsetHeight - 8;
  if (top < 8) top = r.bottom + 8;
  var left = Math.max(8, Math.min(r.left, window.innerWidth - errPop.offsetWidth - 8));
  errPop.style.left = left + 'px';
  errPop.style.top = top + 'px';
}

function hideErrPop(){ if (errPop) errPop.style.display = 'none'; }

// The swap rides one updateContents, so undo reads it as one step.
function fixErrWord(){
  if (!errPop) return;
  var at = parseInt(errPop.getAttribute('data-at'), 10);
  var len = parseInt(errPop.getAttribute('data-len'), 10);
  var sug = errPop.getAttribute('data-sug') || '';
  hideErrPop();
  if (!(at >= 0) || !(len > 0) || !sug) return;
  try { quill.updateContents(new Delta().retain(at).delete(len).insert(sug), 'user'); } catch(e){ return; }
  var shift = sug.length - len;
  activeFlags = activeFlags
    .filter(function(f){ return !(f.index === at && f.length === len); })
    .map(function(f){ return f.index > at
      ? { index: f.index + shift, length: f.length, word: f.word, suggestion: f.suggestion }
      : f; });
}

function ignoreErrWord(){
  if (!errPop) return;
  var word = (errPop.getAttribute('data-word') || '').toLowerCase();
  hideErrPop();
  if (!word) return;
  ignoredWords.add(word);
  for (var i=activeFlags.length-1;i>=0;i--){
    if (activeFlags[i].word.toLowerCase() !== word) continue;
    try { quill.formatText(activeFlags[i].index, activeFlags[i].length, 'ai-err', false, 'silent'); } catch(e){}
    activeFlags.splice(i,1);
  }
}

quill.root.addEventListener('click', function(e){
  var t = e.target;
  if (t && t.closest && t.closest('span.ai-err')){
    e.preventDefault(); e.stopPropagation();
    openErrPop(t.closest('span.ai-err'));
    return;
  }
  hideErrPop();
});
document.addEventListener('mousedown', function(e){
  if (errPop && errPop.style.display === 'block' &&
      !(e.target && e.target.closest && e.target.closest('#err_pop'))) hideErrPop();
  if (aiMenu && aiMenu.style.display === 'block' &&
      !(e.target && e.target.closest && e.target.closest('#ai_menu')) &&
      !(e.target && e.target.closest && e.target.closest('#ai_bubble'))) hideAiMenu();
}, true);
document.addEventListener('keydown', function(e){
  if (e.key === 'Escape'){ hideErrPop(); hideAiMenu(); hideAiNote(); }
}, true);

// ── the loud door: the selection bubble ───────────────────────────────────
function ensureAiBubble(){
  if (aiBubble) return;
  aiBubble = document.createElement('div');
  aiBubble.id = 'ai_bubble';
  var fix = document.createElement('button');
  fix.type = 'button'; fix.id = 'ai_fix_btn';
  fix.textContent = '\uD83E\uDE84 Fix Grammar';
  fix.addEventListener('click', function(){ requestAi('fix'); });
  var rw = document.createElement('button');
  rw.type = 'button'; rw.id = 'ai_rw_btn';
  rw.textContent = '\u270D Rewrite \u25BE';
  rw.addEventListener('click', function(e){
    e.stopPropagation();
    if (aiInFlight) return;
    if (aiMenu && aiMenu.style.display === 'block'){ hideAiMenu(); return; }
    showAiMenu();
  });
  aiBubble.appendChild(fix); aiBubble.appendChild(rw);
  aiMenu = document.createElement('div');
  aiMenu.id = 'ai_menu';
  var styles = [['humanize','Humanize'],['professional','Professional'],['simple','Simple'],
                ['academic','Academic'],['jargon','Jargon'],['lengthen','Lengthen'],['shorten','Shorten']];
  for (var i=0;i<styles.length;i++){
    (function(pair){
      var b = document.createElement('button');
      b.type = 'button'; b.textContent = pair[1];
      b.addEventListener('click', function(){ hideAiMenu(); requestAi('rewrite', pair[0]); });
      aiMenu.appendChild(b);
    })(styles[i]);
  }
  document.body.appendChild(aiBubble);
  document.body.appendChild(aiMenu);
}

function showAiBubble(){
  if (selImg) return;   // a selected picture outranks the bubble
  ensureAiBubble();
  if (aiInFlight){ aiBubble.style.display = 'block'; return; }
  var rect = null;
  try {
    var dsel = document.getSelection();
    if (dsel && dsel.rangeCount && dsel.anchorNode && quill.root.contains(dsel.anchorNode))
      rect = dsel.getRangeAt(0).getBoundingClientRect();
  } catch(e){}
  if (!rect || (!rect.width && !rect.height)){ hideAiBubble(); return; }
  aiBubbleRect = { left: rect.left, top: rect.top, bottom: rect.bottom, width: rect.width };
  positionAiBubble();
  aiBubble.style.display = 'block';
}

function positionAiBubble(){
  if (!aiBubble || !aiBubbleRect) return;
  var x = aiBubbleRect.left + aiBubbleRect.width / 2 - aiBubble.offsetWidth / 2;
  var y = aiBubbleRect.top - aiBubble.offsetHeight - 8;
  if (y < 8) y = aiBubbleRect.bottom + 8;
  x = Math.max(8, Math.min(x, window.innerWidth - aiBubble.offsetWidth - 8));
  aiBubble.style.left = x + 'px';
  aiBubble.style.top = y + 'px';
}

function hideAiBubble(){ if (aiBubble) aiBubble.style.display = 'none'; hideAiMenu(); }

function showAiMenu(){
  ensureAiBubble();
  aiMenu.style.display = 'block';
  var r = aiBubble.getBoundingClientRect();
  var x = Math.max(8, Math.min(r.left, window.innerWidth - aiMenu.offsetWidth - 8));
  var y = r.top - aiMenu.offsetHeight - 6;
  if (y < 8) y = r.bottom + 6;
  aiMenu.style.left = x + 'px';
  aiMenu.style.top = y + 'px';
}
function hideAiMenu(){ if (aiMenu) aiMenu.style.display = 'none'; }

function requestAi(kind, style){
  var sel = quill.getSelection();
  if (!sel || sel.length <= 3) return;
  if (aiInFlight) return;
  var text = quill.getText(sel.index, sel.length).trim();
  if (!text) return;
  if (text.length > 16000) text = text.slice(0, 16000);
  aiInFlight = true;
  aiPending = { index: sel.index, length: sel.length, kind: kind };
  setAiBusy(true);
  if (kind === 'rewrite') post({ type:'ai_rewrite', text: text, style: String(style || 'humanize'), seq: worldSeq });
  else post({ type:'ai_fix_selection', text: text, seq: worldSeq });
}

function setAiBusy(busy){
  if (!aiBubble) return;
  var f = aiBubble.querySelector('#ai_fix_btn'), r = aiBubble.querySelector('#ai_rw_btn');
  if (f) f.style.opacity = busy ? '0.5' : '1';
  if (r) r.style.opacity = busy ? '0.5' : '1';
  if (busy){ aiBubble.style.display = 'block'; hideAiMenu(); }
}

// One answer, one range: the rewrite lands as a single updateContents, so
// undo reads it as one step - and the note whispers when nothing was needed.
function applyAiResult(msg){
  aiInFlight = false;
  setAiBusy(false);
  var seq = (msg && typeof msg.seq === 'number') ? msg.seq|0 : -1;
  if (seq !== worldSeq || !aiPending){ aiPending = null; return; }
  var p = aiPending; aiPending = null;
  if (!msg || !msg.ok){
    showAiNote((msg && msg.message) || 'The request failed - nothing changed.');
    return;
  }
  var text = typeof msg.text === 'string' ? msg.text : '';
  if (!text){
    showAiNote(msg.message || 'Grammar looks good!');
    return;
  }
  if (text.length > 16000) text = text.slice(0, 16000);
  try { quill.updateContents(new Delta().retain(p.index).delete(p.length).insert(text), 'user'); } catch(e){}
}

// -- the typewriter (v1.19.79) --------------------------------------------
// The rewrite streams: the host speaks its deltas, the sheet types them at
// its own pace - a 20ms tick carrying one to ten characters by the size of
// the backlog, SummaryWindow's adaptive buffer ported to the page. The
// pending range is deleted the moment the first delta lands, everything
// after rides at the same index, and the done message may swap the typed
// run for its cleaned self (fences and JSON wrappers never survive).
var streamQueue = '', streamTimer = null, streamStarted = false;
var streamTargetIdx = 0, streamStartIdx = 0;
var streamDonePending = null;

function onAiStreamDelta(msg){
  var seq = (msg && typeof msg.seq === 'number') ? msg.seq|0 : -1;
  if (seq !== worldSeq || !aiPending) return;
  if (!streamStarted){
    streamStarted = true;
    var p = aiPending;
    try { quill.updateContents(new Delta().retain(p.index).delete(p.length), 'user'); } catch(e){}
    streamTargetIdx = p.index;
    streamStartIdx = p.index;
  }
  streamQueue += String(msg.delta || '');
  if (!streamTimer) streamTimer = setInterval(pumpTypewriter, 20);
}

function pumpTypewriter(){
  if (!streamQueue.length){
    if (streamDonePending){
      clearInterval(streamTimer);
      streamTimer = null;
      var done = streamDonePending;
      streamDonePending = null;
      finishAiStream(done);
    }
    return;
  }
  // Adaptive pacing: 1 to 10 characters per tick, the backlog setting the pace.
  var step = Math.max(1, Math.min(10, Math.floor((streamQueue.length + 5) / 6)));
  var slice = streamQueue.slice(0, step);
  streamQueue = streamQueue.slice(step);
  quill.insertText(streamTargetIdx, slice, 'user');
  streamTargetIdx += slice.length;
  try { quill.setSelection(streamTargetIdx, 0, 'silent'); } catch(e){}
}

function onAiStreamDone(msg){
  var seq = (msg && typeof msg.seq === 'number') ? msg.seq|0 : -1;
  if (seq !== worldSeq) return;
  if (!streamStarted){
    // Nothing ever streamed: the answer arrived whole. An error or a quiet
    // note lands here; a real text rides the one-shot applyAiResult path.
    aiInFlight = false;
    setAiBusy(false);
    aiPending = null;
    if (msg && msg.ok === false) showAiNote(msg.message || 'The request failed - nothing changed.');
    else if (msg && msg.text) applyAiResult(msg);
    else showAiNote((msg && msg.message) || 'Grammar looks good!');
    return;
  }
  streamDonePending = msg;
}

function finishAiStream(msg){
  aiInFlight = false;
  setAiBusy(false);
  aiPending = null;
  streamStarted = false;
  if (!msg || msg.ok === false){
    showAiNote((msg && msg.message) || 'The request failed - nothing changed.');
    reportState();
    return;
  }
  var text = (msg && typeof msg.text === 'string') ? msg.text : '';
  if (!text){
    showAiNote((msg && msg.message) || 'Grammar looks good!');
    reportState();
    return;
  }
  var typedLen = streamTargetIdx - streamStartIdx;
  if (text !== quill.getText(streamStartIdx, typedLen)){
    try {
      quill.updateContents(new Delta().retain(streamStartIdx).delete(typedLen).insert(text), 'user');
    } catch(e){}
  }
  try { quill.setSelection(streamStartIdx + text.length, 0, 'silent'); } catch(e){}
  reportState();
}

function showAiNote(message){
  if (!aiNote){
    aiNote = document.createElement('div');
    aiNote.id = 'ai_note';
    document.body.appendChild(aiNote);
  }
  aiNote.textContent = String(message || '');
  aiNote.style.display = 'block';
  if (aiNoteTimer) clearTimeout(aiNoteTimer);
  aiNoteTimer = setTimeout(hideAiNote, 2600);
}
function hideAiNote(){ if (aiNote) aiNote.style.display = 'none'; }

quill.on('selection-change', function(range){
  if (aiInFlight) return;
  if (range && range.length > 3 && !selImg) showAiBubble();
  else hideAiBubble();
});

// The bubble follows its selection across scrolls and leaves on resize; the
// card above a red word steps aside on either - the next click re-opens it.
window.addEventListener('scroll', function(){
  if (aiBubble && aiBubble.style.display === 'block' && !aiInFlight){
    try {
      var sel = quill.getSelection();
      var dsel = document.getSelection();
      if (sel && sel.length > 3 && dsel && dsel.rangeCount && dsel.anchorNode && quill.root.contains(dsel.anchorNode)){
        var rect = dsel.getRangeAt(0).getBoundingClientRect();
        if (rect && (rect.width || rect.height)){
          aiBubbleRect = { left: rect.left, top: rect.top, bottom: rect.bottom, width: rect.width };
          positionAiBubble();
        }
      } else hideAiBubble();
    } catch(e){ hideAiBubble(); }
  }
  if (errPop && errPop.style.display === 'block') hideErrPop();
}, true);
window.addEventListener('resize', function(){
  if (aiBubble && aiBubble.style.display === 'block' && !aiInFlight) hideAiBubble();
  if (errPop && errPop.style.display === 'block') hideErrPop();
});

// ── the journal's breath (v1.19.78) ───────────────────────────────────────
// Axo's line spacing lives on two CSS variables the Spacing button turns:
// three speeds, one press ahead each time - Compact, Normal, Relaxed -
// and the paragraph and the lists all breathe by the same two numbers.
var SPACING_MODES = ['compact', 'normal', 'relaxed'];
var SPACING_STYLE = {
  compact: { line: '1.25', pm: '0.15em' },
  normal:  { line: '1.6',  pm: '0.5em'  },
  relaxed: { line: '1.8',  pm: '1.0em'  }
};
var spacingMode = 'normal';
function applySpacing(mode){
  if (!SPACING_STYLE[mode]) mode = 'normal';
  spacingMode = mode;
  var s = SPACING_STYLE[mode];
  quill.root.style.setProperty('--line-height', s.line);
  quill.root.style.setProperty('--p-margin', s.pm);
}
function cycleSpacing(){
  applySpacing(SPACING_MODES[(SPACING_MODES.indexOf(spacingMode) + 1) % SPACING_MODES.length]);
}
applySpacing('normal');

// Axo's align law: the chord sets the voice, a second press walks it
// back to the page's own left.
function align(mode){
  var f = quill.getFormat();
  var next = (mode === 'left' || f.align === mode) ? false : mode;
  quill.format('align', next, 'user');
  reportState();
}

// ── the journal's smart typing (v1.19.78) ──────────────────────────────────
// Axo's two quiet laws, ported: the standalone "i" grows up the moment a
// space follows it, and a sentence's first letter stands up at the start
// of a block or after a period, an exclamation or a question and one or
// more spaces. Both fix the keystroke BEFORE the model sees it - one
// dispatch, one undo step, the same single transaction Axo's
// handleTextInput gave the journal. v1.19.79 fixes both laws' caret: every
// synthetic insert now walks Quill's own selection forward (the missing
// advance was typing "dog" into "god"), and the sentence-start test counts
// real sentence ends only - [.!?] plus spaces - not every single space.
// Capture on the editor root, above
// Quill's own handlers; chords and composition never enter here.
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

// ── the ribbon's commands ─────────────────────────────────────────────────
function toggle(name){
  var f = quill.getFormat();
  quill.format(name, !f[name], 'user');
  reportState();
}
function sizeStep(dir){
  var cur = parseFloat(effSize()) || 12, next = null, i;
  if (dir > 0){
    for (i=0;i<LADDER.length;i++) if (LADDER[i] > cur + 0.01){ next = LADDER[i]; break; }
    if (next === null) next = LADDER[LADDER.length-1];
  } else {
    for (i=LADDER.length-1;i>=0;i--) if (LADDER[i] < cur - 0.01){ next = LADDER[i]; break; }
    if (next === null) next = LADDER[0];
  }
  quill.format('size', next + 'pt', 'user');
  reportState();
}

// -- blocks: headers, lists, the alpha sub-numbers, the quote -------------
// Every one of these is a TOGGLE (v1.19.73): the same button that dresses a
// line undresses it again - the header returns to body text, the list
// dissolves, the quote comes out of its block, the sub-number walks back
// one level. The reader's own law: press once for on, again for off.
function header(level){
  var f = quill.getFormat();
  quill.format('header', f.header === level ? false : level, 'user');
  reportState();
}
function bullet(){
  var f = quill.getFormat();
  quill.format('list', f.list === 'bullet' ? false : 'bullet', 'user');
  reportState();
}
function number(){
  var f = quill.getFormat();
  quill.format('list', f.list === 'ordered' ? false : 'ordered', 'user');
  reportState();
}
function subnumber(){
  var f = quill.getFormat();
  if (f.list){
    // Already in a list: one press nests the line (the alpha ladder from
    // the CSS counter), another press walks it back out.
    quill.format('indent', (f.indent|0) > 0 ? '-1' : '+1', 'user');
  } else {
    // Not in a list yet: start one, already one level deep.
    quill.format('list', 'ordered', 'user');
    quill.format('indent', '+1', 'user');
  }
  reportState();
}
function quote(){
  // v1.19.79: a selection gets typographic quotes of its own - wrap, or
  // wrap OFF when the words already wear them; a resting caret still
  // toggles the block quote on the current block, as always.
  var sel = quill.getSelection();
  if (sel && sel.length > 0){
    var text = quill.getText(sel.index, sel.length);
    var LQ = '\u201C', RQ = '\u201D';
    if ((text.indexOf(LQ) === 0 && text.lastIndexOf(RQ) === text.length - 1) ||
        (text.charAt(0) === '"' && text.charAt(text.length - 1) === '"')){
      var unquoted = text.slice(1, -1);
      quill.deleteText(sel.index, sel.length, 'user');
      quill.insertText(sel.index, unquoted, { bold: false, italic: false }, 'user');
      quill.setSelection(sel.index, unquoted.length, 'user');
    } else {
      var quoted = LQ + text + RQ;
      quill.deleteText(sel.index, sel.length, 'user');
      quill.insertText(sel.index, quoted, { bold: true, italic: true }, 'user');
      quill.setSelection(sel.index, quoted.length, 'user');
    }
    reportState();
    return;
  }
  var f = quill.getFormat();
  quill.format('blockquote', !f.blockquote, 'user');
  reportState();
}

// -- the clipboard the host forwards --------------------------------------
// The Windows clipboard belongs to the host: copy and cut hand the
// selection's text UP (type 'clip') and cut takes it out of the document;
// paste wears whatever came DOWN - a rich HTML fragment first, plain text
// as the fallback. A copy with nothing selected touches nothing.
function copySelection(cut){
  var sel = quill.getSelection();
  if (!sel || !sel.length){ if (cut) reportState(); return; }
  var text = quill.getText(sel.index, sel.length);
  if (cut){
    quill.deleteText(sel.index, sel.length, 'user');
    reportState();
  }
  post({ type:'clip', kind: cut ? 'cut' : 'copy', text: text });
}
function pasteFromHost(msg){
  var sel = quill.getSelection(true);
  var at = sel ? sel.index : quill.getLength();
  if (msg.html && String(msg.html).length){
    try { quill.clipboard.dangerouslyPasteHTML(at, String(msg.html), 'user'); return; } catch(e){}
  }
  var text = String(msg.text || '');
  if (text){
    quill.insertText(at, text, 'user');
    try { quill.setSelection(at + text.length, 0, 'user'); } catch(e){}
  }
}
if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener){
  window.chrome.webview.addEventListener('message', function(e){
    var msg = e && e.data;
    if (!msg || !msg.cmd) return;
    switch (msg.cmd) {
      case 'bold': toggle('bold'); break;
      case 'italic': toggle('italic'); break;
      case 'underline': toggle('underline'); break;
      case 'strike': toggle('strike'); break;
      case 'font': if (msg.name){ quill.format('font', slug(String(msg.name)), 'user'); reportState(); } break;
      case 'size': if (msg.pt){ quill.format('size', pt(String(msg.pt)), 'user'); reportState(); } break;
      case 'sizeStep': sizeStep(Number(msg.dir) || 0); break;
      case 'linkui': openLinkPop(); break;
      case 'footnote': insertFootnote(); break;
      case 'image': insertImage(String(msg.src || '')); break;
      case 'undo': quill.history.undo(); reportState(); break;
      case 'redo': quill.history.redo(); reportState(); break;
      case 'dump': saveNow(); break;
      case 'scroll': try { window.scrollTo({ top: 0, behavior: 'smooth' }); } catch(err){ window.scrollTo(0, 0); } break;
      case 'focus': quill.focus(); break;
      case 'header': header(Number(msg.level) || 1); break;
      case 'bullet': bullet(); break;
      case 'number': number(); break;
      case 'subnumber': subnumber(); break;
      case 'quote': quote(); break;
      case 'spacing': cycleSpacing(); break;
      case 'alignLeft': quill.format('align', false, 'user'); reportState(); break;
      case 'alignCenter': quill.format('align', 'center', 'user'); reportState(); break;
      case 'alignJustify': quill.format('align', 'justify', 'user'); reportState(); break;
      case 'invert': document.body.classList.toggle('az-inv', !!msg.on); break;
      case 'selectAll': quill.focus(); quill.setSelection(0, quill.getLength(), 'user'); break;
      case 'copy': copySelection(false); break;
      case 'cut': copySelection(true); break;
      case 'paste': pasteFromHost(msg); break;
      case 'pasteImage': insertImage(String(msg.src || '')); break;
      case 'aiScanResult': applyScanResult(msg); break;
      case 'aiResult': applyAiResult(msg); break;
      case 'aiStreamDelta': onAiStreamDelta(msg); break;
      case 'aiStreamDone': onAiStreamDone(msg); break;
      case 'load': loadWorld(String(msg.html || ''), msg.seq|0); break;
      case 'i18n': i18n = { apply: String(msg.apply || 'Apply'), remove: String(msg.remove || 'Remove'),
                            linkUrl: String(msg.linkUrl || 'Link URL') };
                    lpUrl.placeholder = i18n.linkUrl;
                    break;
    }
  });
}

// The stage is set the moment the sheet lands; the host answers with the
// world to wear (load) or an empty page to start writing into (focus).
post({ type:'pages', count: 1 });
post({ type:'ready' });
postThumbs();
})();
</script>
</body>
</html>
""";
    }
}
