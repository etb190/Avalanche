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
    // popover, image paste and drop, and a page raster for the sidebar's rail.
    // Deliberately 100% offline: Quill rides as WPF resources
    // (Resources/Scripts/quill.min.js + quill.snow.css) and is inlined into the
    // document at first use - no CDN, ever.
    // Communication contract with the ribbon:
    //   in  {cmd:...}  bold|italic|underline|strike|font|size|sizeStep|linkui|
    //                  footnote|image|undo|redo|load|dump|scroll|focus|i18n
    //   out {type:...} ready|state|save|link|title|pages|thumbs
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
               font-family:'Times New Roman',serif; font-size:12pt; line-height:1.5;
               color:#1c1c1c; padding:64px 72px 32px 72px; outline:none; }
  .ql-editor.ql-blank::before { content:none; }
  .ql-editor img { max-width:100%; height:auto; }
  .ql-editor a { cursor:pointer; color:#1155cc; text-decoration:underline; }
  sup.fnref { color:#1155cc; cursor:pointer; }
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
  quill.insertEmbed(sel ? sel.index : quill.getLength(), 'image', src, 'user');
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
  post({ type:'state', edit:true, font: effFont(), size: effSize(),
         b: !!f.bold, i: !!f.italic, u: !!f.underline, s: !!f.strike, page: 1 });
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
  var delta = JSON.stringify(quill.getContents()).replace(/</g, '\\u003c');
  return '<section data-az="doc">' + quill.root.innerHTML + '</section>' +
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
  '.docbody a{color:#1155cc;text-decoration:underline;}' +
  'sup.fnref{color:#1155cc;}sup.fnref::after{content:attr(data-n);}' +
  ".docfn{margin:0 72px;padding:12px 0 0 0;border-top:1px solid #d8d8d8;font-family:'Segoe UI',sans-serif;font-size:10pt;color:#333;}" +
  '.docfn .fnnum{font-weight:bold;color:#1155cc;margin-right:7px;}';
function postThumbs(){
  try {
    // Chromium's SVG-as-image refuses subresource loads - even data: URIs -
    // so the raster wears a neutral block where an image stood; every other
    // mark (fonts, sizes, links, footnotes) paints for real.
    var docBody = quill.root.innerHTML.replace(/<img\b[^>]*>/gi,
      '<span style="display:inline-block;width:88px;height:22px;background:#e4e4e4;border-radius:2px;vertical-align:middle;"></span>');
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
