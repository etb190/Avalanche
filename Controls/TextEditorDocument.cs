using System;

namespace Avalanche.Controls
{
    // The text editor's page (v1.19.64): one embedded HTML document carrying the
    // whole writing surface. Real Letter-sized pages that grow on their own - text
    // that overflows a page walks to the next one, and a trailing empty page steps
    // back off the stage - text that lives on its own layer above images that are
    // dragged freely behind it, footnotes that anchor to the page they are written
    // on, hyperlinks that hand themselves to the host, and a caret whose font and
    // size report home so the ribbon always shows what the reader is wearing.
    // Deliberately dependency-free: no editor framework, no CDN, just the platform
    // the browser pane already ships. Communication contract with the ribbon:
    //   in  {cmd:...}  bold|italic|underline|strike|font|size|linkui|footnote|image|scroll|focus|load|i18n
    //   out {type:...} ready|pages|state|save|link
    public static class TextEditorDocument
    {
        public const string Html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
  html, body { margin:0; padding:0; }
  body { background:#3d4046; overflow-x:hidden; font-family:'Segoe UI',sans-serif; }
  #docs { padding:16px 0 42px 0; }
  .page { width:816px; height:1056px; margin:0 auto 18px auto; background:#ffffff;
          box-shadow:0 2px 10px rgba(0,0,0,0.45); position:relative; border-radius:2px; }
  .pin  { position:absolute; left:0; top:0; right:0; bottom:0; box-sizing:border-box;
          padding:64px 72px 78px 72px; overflow:hidden; }
  .txt  { min-height:100%; position:relative; z-index:2; outline:none; color:#1c1c1c;
          font-family:'Times New Roman',serif; font-size:12pt; line-height:1.5;
          user-select:text; -webkit-user-select:text; caret-color:#1c1c1c; }
  .txt a { color:#2b6cb0; }
  .fnnote { position:absolute; left:72px; right:72px; bottom:26px; z-index:2; outline:none;
            border-top:1px solid #c9c9c9; padding-top:5px; color:#333333;
            font-family:'Times New Roman',serif; font-size:9.5pt; line-height:1.45;
            user-select:text; -webkit-user-select:text; caret-color:#333333; display:none; }
  .fnmark { vertical-align:super; font-size:0.68em; color:#4a90d9; cursor:default; }
  .fnnum  { color:#4a90d9; margin-right:4px; }
  .fimg { position:absolute; z-index:1; cursor:move; user-select:none; -webkit-user-drag:none; touch-action:none; }
  .fimg img { width:100%; height:auto; display:block; pointer-events:none; }
  .fimg.sel { outline:2px solid #4a90d9; outline-offset:2px; }
  .rsz { position:absolute; right:-7px; bottom:-7px; width:14px; height:14px; background:#4a90d9;
         border:2px solid #ffffff; border-radius:2px; cursor:nwse-resize; display:none; }
  .fimg.sel .rsz { display:block; }
  #linkpop { position:fixed; display:none; z-index:50; background:#ffffff; border:1px solid #c9c9c9;
             border-radius:6px; box-shadow:0 4px 16px rgba(0,0,0,0.3); padding:8px; }
  #linkpop input { width:250px; padding:5px 8px; border:1px solid #c9c9c9; border-radius:4px;
                   font-size:12px; outline:none; font-family:'Segoe UI',sans-serif; }
  #linkpop button { padding:5px 12px; margin-left:6px; border:1px solid #c9c9c9; background:#f4f4f4;
                    border-radius:4px; font-size:12px; cursor:pointer; font-family:'Segoe UI',sans-serif; }
</style>
</head>
<body>
<div id="docs"></div>
<div id="linkpop">
  <input id="linkurl" type="text" spellcheck="false">
  <button id="linkok" type="button"></button>
  <button id="linkrm" type="button"></button>
</div>
<script>
(function(){
  'use strict';
  var docs = document.getElementById('docs');
  var ZWSP = '\u200B';
  var saved = null;          // {el, range} - the caret's last known resting place
  var pickedImage = null;    // the selected image wrapper
  var drag = null;           // an active image move / resize
  var linkTarget = null;     // the <a> being edited, or null for an insert
  var linkRange = null;      // the range a new link will own
  var FNID = 0;
  var saveTimer = null, stateTimer = null;
  var i18n = { apply:'Apply', remove:'Remove', linkUrl:'' };

  function post(o){ try { window.chrome.webview.postMessage(JSON.stringify(o)); } catch(e){} }

  // -- pages ---------------------------------------------------------------
  function makePage(){
    var page = document.createElement('div'); page.className = 'page';
    var pin  = document.createElement('div'); pin.className  = 'pin';
    var txt  = document.createElement('div'); txt.className  = 'txt';
    txt.contentEditable = 'true'; txt.spellcheck = false;
    var fn   = document.createElement('div'); fn.className   = 'fnnote';
    fn.contentEditable = 'true'; fn.spellcheck = false; fn.dataset.empty = '1';
    pin.appendChild(txt); pin.appendChild(fn); page.appendChild(pin);
    docs.appendChild(page);
    return page;
  }
  function txtOf(p){ return p.querySelector('.txt'); }
  function fnOf(p){ return p.querySelector('.fnnote'); }
  function pageAt(n){ return docs.children[n] || null; }
  function overflow(t){ return t.scrollHeight > t.clientHeight + 1; }

  // Text that no longer fits walks forward, whole block by whole block, until
  // every page is honest about what it holds. A fresh page is born when the
  // last one is full; the cascade moves page by page so a mid-document edit
  // pushes everything after it down the stack.
  function reflowFrom(page){
    var guard = 0;
    while (page && guard++ < 400){
      var t = txtOf(page);
      if (!overflow(t)) break;
      var next = page.nextElementSibling;
      if (!next) next = makePage();
      var nt = txtOf(next);
      var last = t.lastElementChild;
      if (last){ nt.insertBefore(last, nt.firstChild); }
      else { while (t.firstChild) nt.insertBefore(t.firstChild, nt.firstChild); }
      // Stay on the same page until it is honest again, then let the cascade
      // continue from the neighbor we just fed - a large paste walks forward
      // page by page in one pass.
      page = overflow(t) ? page : next;
    }
  }

  // A trailing page whose text, images and footnotes all emptied steps back
  // off the stage - unless the caret is standing on it right now.
  function trimTrailing(){
    var guard = 0;
    while (docs.children.length > 1 && guard++ < 50){
      var p = docs.children[docs.children.length - 1];
      var t = txtOf(p), f = fnOf(p);
      var sel = document.getSelection();
      var caretInside = sel && sel.anchorNode && p.contains(sel.anchorNode);
      var hasImg = p.querySelector('.fimg');
      if (!caretInside && !hasImg && !t.querySelector('img')
          && t.textContent.split(ZWSP).join('').trim() === '' && f.dataset.empty === '1'){
        p.parentNode.removeChild(p);
      } else break;
    }
  }

  function reportPages(){ post({ type:'pages', count: docs.children.length }); }

  function afterEdit(page){
    if (page) reflowFrom(page);
    trimTrailing();
    renumberFns();
    reportPages();
    scheduleSave();
  }

  // -- caret bookkeeping -----------------------------------------------------
  function editableOf(node){
    if (!node) return null;
    var el = node.nodeType === 1 ? node : node.parentElement;
    return el ? el.closest('.txt,.fnnote') : null;
  }

  document.addEventListener('selectionchange', function(){
    var sel = document.getSelection();
    if (sel && sel.rangeCount){
      var ed = editableOf(sel.anchorNode);
      if (ed) saved = { el: ed, range: sel.getRangeAt(0).cloneRange() };
    }
    scheduleState();
  });

  // The ribbon's click took the focus; the caret's place is handed back before
  // any command runs, so the buttons act on exactly what the reader selected.
  function restoreSelection(){
    if (!saved) return null;
    if (!document.body.contains(saved.el)){ saved = null; return null; }
    var sel = document.getSelection();
    saved.el.focus();
    sel.removeAllRanges(); sel.addRange(saved.range);
    return saved;
  }

  function placeCaret(t, atStart){
    t.focus();
    var r = document.createRange(); r.selectNodeContents(t); r.collapse(atStart);
    var sel = document.getSelection(); sel.removeAllRanges(); sel.addRange(r);
    saved = { el: t, range: r.cloneRange() };
  }

  function caretAtEdge(t, atEnd){
    var sel = document.getSelection();
    if (!sel.rangeCount) return false;
    var r = sel.getRangeAt(0);
    if (!r.collapsed) return false;
    var probe = document.createRange(); probe.selectNodeContents(t);
    if (atEnd) probe.setEnd(r.endContainer, r.endOffset);
    else probe.setStart(r.startContainer, r.startOffset);
    return probe.toString().split(ZWSP).join('').length === 0;
  }

  document.addEventListener('keydown', function(e){
    if (pickedImage && (e.key === 'Delete' || e.key === 'Backspace')){
      e.preventDefault();
      pickedImage.parentNode.removeChild(pickedImage);
      deselectImage();
      afterEdit(null);
      return;
    }
    if (e.altKey || e.ctrlKey || e.metaKey) return;
    var ed = e.target && editableOf(e.target);
    if (!ed || !ed.classList.contains('txt')) return;
    var page = ed.closest('.page');
    if ((e.key === 'ArrowDown' || e.key === 'ArrowRight') && caretAtEdge(ed, true) && page.nextElementSibling){
      e.preventDefault(); placeCaret(txtOf(page.nextElementSibling), true);
    } else if ((e.key === 'ArrowUp' || e.key === 'ArrowLeft') && caretAtEdge(ed, false) && page.previousElementSibling){
      e.preventDefault(); placeCaret(txtOf(page.previousElementSibling), false);
    }
  });

  // -- saving (debounced; the host writes the session cache) ------------------
  function scheduleSave(){ if (saveTimer) clearTimeout(saveTimer); saveTimer = setTimeout(saveNow, 700); }
  function scheduleState(){ if (stateTimer) clearTimeout(stateTimer); stateTimer = setTimeout(reportState, 50); }
  function saveNow(){ saveTimer = null; post({ type:'save', html: docs.innerHTML }); }

  document.addEventListener('input', function(e){
    var ed = editableOf(e.target); if (!ed) return;
    var page = ed.closest('.page');
    if (ed.classList.contains('txt')){
      cleanZwsp(ed);
      afterEdit(page);
    } else {
      renumberFns();
      scheduleSave();
    }
  });

  // A size span holds a zero-width crutch so the caret has something to stand
  // on before any text arrives; the crutch leaves the moment real text lands.
  function cleanZwsp(root){
    var spans = root.querySelectorAll('span');
    for (var i = 0; i < spans.length; i++){
      var sp = spans[i];
      if (sp.querySelector('*')) continue;
      var text = '';
      sp.childNodes.forEach(function(n){ if (n.nodeType === 3) text += n.nodeValue; });
      if (text.indexOf(ZWSP) < 0) continue;
      var real = text.split(ZWSP).join('');
      if (real.length > 0){
        sp.childNodes.forEach(function(n){
          if (n.nodeType === 3) n.nodeValue = n.nodeValue.split(ZWSP).join('');
        });
      }
    }
  }

  // -- footnotes ---------------------------------------------------------------
  function insertFootnote(){
    var s = restoreSelection(); if (!s) return;
    if (!s.el.classList.contains('txt')) return;
    var id = 'fn' + (++FNID);
    var mark = document.createElement('sup');
    mark.className = 'fnmark'; mark.contentEditable = 'false'; mark.dataset.fn = id;
    mark.textContent = '?';
    s.range.insertNode(mark);
    var nr = document.createRange(); nr.setStartAfter(mark); nr.collapse(true);
    var sel = document.getSelection(); sel.removeAllRanges(); sel.addRange(nr);
    saved = { el: s.el, range: nr.cloneRange() };
    var page = s.el.closest('.page');
    var fn = fnOf(page);
    var entry = document.createElement('div'); entry.className = 'fnentry'; entry.dataset.fn = id;
    var num = document.createElement('span'); num.className = 'fnnum';
    var body = document.createElement('span'); body.className = 'fnbody';
    entry.appendChild(num); entry.appendChild(body); fn.appendChild(entry);
    syncFnote(page);
    renumberFns();
    var br = document.createRange(); br.selectNodeContents(body); br.collapse(true);
    fn.focus(); sel.removeAllRanges(); sel.addRange(br);
    saved = { el: fn, range: br.cloneRange() };
    afterEdit(null);
  }

  function syncFnote(page){
    var fn = fnOf(page); if (!fn) return;
    var has = fn.querySelector('.fnentry');
    fn.style.display = has ? 'block' : 'none';
    fn.dataset.empty = has ? '0' : '1';
  }

  // Every footnote in the document gets its place in reading order; an entry
  // whose marker was deleted walks off with its number, and an entry emptied
  // of text (by anything but the hand currently typing in it) takes its marker
  // with it.
  function renumberFns(){
    var sel = document.getSelection();
    docs.querySelectorAll('.fnentry').forEach(function(en){
      var bodyEl = en.querySelector('.fnbody');
      var caretIn = sel && sel.anchorNode && en.contains(sel.anchorNode);
      if (!caretIn && bodyEl && bodyEl.textContent.trim() === ''){
        var mk = docs.querySelector('.fnmark[data-fn="' + en.dataset.fn + '"]');
        if (mk) mk.parentNode.removeChild(mk);
        en.parentNode.removeChild(en);
      }
    });
    var marks = {};
    var idx = 0;
    docs.querySelectorAll('.fnmark').forEach(function(m){
      idx++;
      var id = m.dataset.fn || ('fn' + (++FNID));
      m.dataset.fn = id;
      m.textContent = String(idx);
      marks[id] = idx;
    });
    docs.querySelectorAll('.fnentry').forEach(function(en){
      var id = en.dataset.fn;
      if (marks[id] === undefined){ en.parentNode.removeChild(en); return; }
      var num = en.querySelector('.fnnum');
      if (num) num.textContent = marks[id] + '.';
    });
    for (var i = 0; i < docs.children.length; i++) syncFnote(docs.children[i]);
  }

  // -- hyperlinks ---------------------------------------------------------------
  function linkUi(){
    var s = restoreSelection(); if (!s) return;
    var node = s.range.startContainer;
    var el = node.nodeType === 1 ? node : node.parentElement;
    var a = el ? el.closest('a') : null;
    linkTarget = a;
    linkRange = a ? null : s.range.cloneRange();
    var pop = document.getElementById('linkpop');
    var inp = document.getElementById('linkurl');
    inp.value = a ? (a.getAttribute('href') || '') : '';
    var rect = null;
    try { rect = s.range.getBoundingClientRect(); } catch(e){}
    if (!rect || (rect.width === 0 && rect.height === 0)){
      var cr = s.el.getBoundingClientRect();
      rect = { left: cr.left, top: cr.top, width: 0, height: cr.height, bottom: cr.bottom };
    }
    pop.style.display = 'block';
    var pw = 400, ph = 46;
    var x = Math.max(8, Math.min(rect.left, window.innerWidth - pw - 8));
    var y = rect.top - ph - 8; if (y < 8) y = rect.bottom + 8;
    pop.style.left = x + 'px'; pop.style.top = y + 'px';
    setTimeout(function(){ inp.focus(); inp.select(); }, 30);
  }

  function applyLink(){
    var inp = document.getElementById('linkurl');
    var url = inp.value.trim();
    hideLinkPop();
    if (linkTarget){
      if (!url){ unwrap(linkTarget); }
      else { linkTarget.setAttribute('href', url); }
      linkTarget = null;
      afterEdit(null);
      return;
    }
    if (!url || !saved) return;
    if (!/^(https?:|mailto:|file:)/i.test(url)) url = 'https://' + url;
    var s = saved;
    s.el.focus();
    var sel = document.getSelection();
    sel.removeAllRanges(); sel.addRange(linkRange);
    if (linkRange.collapsed){
      var a = document.createElement('a'); a.href = url; a.textContent = url;
      linkRange.insertNode(a);
      var nr = document.createRange(); nr.setStartAfter(a); nr.collapse(true);
      sel.removeAllRanges(); sel.addRange(nr);
      saved = { el: s.el, range: nr.cloneRange() };
    } else {
      document.execCommand('createLink', false, url);
    }
    afterEdit(null);
  }

  function unwrap(a){
    var parent = a.parentNode;
    while (a.firstChild) parent.insertBefore(a.firstChild, a);
    parent.removeChild(a);
  }

  function hideLinkPop(){ document.getElementById('linkpop').style.display = 'none'; }

  document.getElementById('linkok').addEventListener('click', function(){ applyLink(); });
  document.getElementById('linkrm').addEventListener('click', function(){
    var t = linkTarget;
    hideLinkPop();
    if (t){ unwrap(t); linkTarget = null; afterEdit(null); }
  });
  document.getElementById('linkurl').addEventListener('keydown', function(e){
    if (e.key === 'Enter'){ e.preventDefault(); applyLink(); }
    else if (e.key === 'Escape'){ hideLinkPop(); }
  });

  docs.addEventListener('click', function(e){
    var a = e.target.closest ? e.target.closest('a') : null;
    if (!a) return;
    e.preventDefault();
    var href = a.getAttribute('href');
    if (href && (e.ctrlKey || e.metaKey)) post({ type:'link', url: href });
  });

  // -- images behind the text ----------------------------------------------------
  function deselectImage(){
    if (pickedImage) pickedImage.classList.remove('sel');
    pickedImage = null;
  }

  function insertImage(src){
    var s = restoreSelection();
    var page = s ? s.el.closest('.page') : pageAt(docs.children.length - 1);
    if (!page) page = makePage();
    var pin = page.querySelector('.pin');
    var wrap = document.createElement('div'); wrap.className = 'fimg';
    var img = document.createElement('img'); img.src = src; img.draggable = false;
    var rsz = document.createElement('div'); rsz.className = 'rsz';
    wrap.appendChild(img); wrap.appendChild(rsz); pin.appendChild(wrap);
    img.addEventListener('load', function(){
      var maxW = pin.clientWidth - 120;
      var w = Math.min(img.naturalWidth || 320, maxW, 420);
      wrap.style.width = w + 'px';
      wrap.style.left = Math.round((pin.clientWidth - w) / 2) + 'px';
      wrap.style.top = '170px';
      selectImage(wrap);
      scheduleSave();
    });
  }

  function selectImage(img){
    deselectImage();
    pickedImage = img;
    img.classList.add('sel');
  }

  docs.addEventListener('pointerdown', function(e){
    var f = e.target.closest ? e.target.closest('.fimg') : null;
    if (f){
      e.preventDefault(); e.stopPropagation();
      selectImage(f);
      var rszHit = e.target.classList && e.target.classList.contains('rsz');
      var pin = f.parentElement;
      var rect = f.getBoundingClientRect(), prect = pin.getBoundingClientRect();
      drag = { mode: rszHit ? 'size' : 'move', el: f, pin: pin,
               startX: e.clientX, startY: e.clientY,
               origL: rect.left - prect.left, origT: rect.top - prect.top,
               origW: rect.width };
      scheduleSave();
      return;
    }
    if (pickedImage) deselectImage();
  });
  docs.addEventListener('pointermove', function(e){
    if (!drag) return;
    var dx = e.clientX - drag.startX, dy = e.clientY - drag.startY;
    if (drag.mode === 'move'){
      var maxL = drag.pin.clientWidth - drag.el.offsetWidth;
      var maxT = drag.pin.clientHeight - drag.el.offsetHeight;
      drag.el.style.left = Math.max(0, Math.min(drag.origL + dx, Math.max(0, maxL))) + 'px';
      drag.el.style.top  = Math.max(0, Math.min(drag.origT + dy, Math.max(0, maxT))) + 'px';
    } else {
      drag.el.style.width = Math.max(60, drag.origW + dx) + 'px';
    }
  });
  docs.addEventListener('pointerup', function(){ if (drag){ drag = null; scheduleSave(); } });

  // -- ribbon commands -------------------------------------------------------------
  function exec(cmd, val){
    var s = restoreSelection(); if (!s) return;
    document.execCommand(cmd, false, val || null);
    afterEdit(s.el.closest('.page'));
    scheduleState();
  }

  function applySize(pt){
    var s = restoreSelection(); if (!s) return;
    if (s.range.collapsed){
      var span = document.createElement('span');
      span.style.fontSize = pt + 'pt';
      span.appendChild(document.createTextNode(ZWSP));
      s.range.insertNode(span);
      var r = document.createRange(); r.setStart(span.firstChild, 1); r.collapse(true);
      var sel = document.getSelection(); sel.removeAllRanges(); sel.addRange(r);
      saved = { el: s.el, range: r.cloneRange() };
      afterEdit(s.el.closest('.page'));
    } else {
      document.execCommand('fontSize', false, '7');
      var fonts = s.el.querySelectorAll('font[size="7"]');
      for (var i = 0; i < fonts.length; i++){
        var f = fonts[i];
        var sp = document.createElement('span');
        sp.style.fontSize = pt + 'pt';
        while (f.firstChild) sp.appendChild(f.firstChild);
        f.parentNode.replaceChild(sp, f);
      }
      afterEdit(s.el.closest('.page'));
    }
    scheduleState();
  }

  // -- state report: what the caret is wearing, where it stands -------------------
  function firstFamily(list){
    if (!list) return '';
    return list.split(',')[0].replace(/["']/g, '').trim();
  }

  function reportState(){
    var sel = document.getSelection();
    var info = { type:'state', font:'', size:'', b:false, i:false, u:false, s:false, edit:false, page:1 };
    var el = (sel && sel.rangeCount)
      ? (sel.anchorNode.nodeType === 1 ? sel.anchorNode : sel.anchorNode.parentElement) : null;
    var ed = el ? el.closest('.txt,.fnnote') : null;
    if (ed){
      info.edit = true;
      var cs = getComputedStyle(el);
      info.font = firstFamily(cs.fontFamily);
      info.size = String(Math.round(parseFloat(cs.fontSize) * 3 / 4));
      try {
        info.b = document.queryCommandState('bold');
        info.i = document.queryCommandState('italic');
        info.u = document.queryCommandState('underline');
        info.s = document.queryCommandState('strikeThrough');
      } catch(e){}
    }
    var host = ed || (saved ? saved.el : null);
    var pg = host ? host.closest('.page') : null;
    if (pg) info.page = Array.prototype.indexOf.call(docs.children, pg) + 1;
    post(info);
  }

  // -- host messages -----------------------------------------------------------------
  function handle(msg){
    switch (msg.cmd){
      case 'bold': exec('bold'); break;
      case 'italic': exec('italic'); break;
      case 'underline': exec('underline'); break;
      case 'strike': exec('strikeThrough'); break;
      case 'font': if (msg.name) exec('fontName', String(msg.name)); break;
      case 'size': applySize(String(msg.pt || '12')); break;
      case 'linkui': linkUi(); break;
      case 'footnote': insertFootnote(); break;
      case 'image': insertImage(String(msg.src || '')); break;
      case 'scroll': {
        var p = pageAt((msg.n | 0) - 1);
        if (p) p.scrollIntoView({ behavior:'smooth', block:'start' });
        break;
      }
      case 'focus': {
        if (!saved && docs.children.length){
          var t = txtOf(docs.children[0]);
          if (t) placeCaret(t, true);
        }
        break;
      }
      case 'load': {
        docs.innerHTML = String(msg.html || '');
        if (!docs.children.length) makePage();
        FNID = 0;
        docs.querySelectorAll('[data-fn]').forEach(function(el){
          var m = /^fn(\d+)$/.exec(el.dataset.fn || '');
          if (m) FNID = Math.max(FNID, parseInt(m[1], 10));
        });
        renumberFns();
        reportPages();
        scheduleSave();
        break;
      }
      case 'i18n': {
        i18n = { apply: String(msg.apply || 'Apply'), remove: String(msg.remove || 'Remove'),
                 linkUrl: String(msg.linkUrl || '') };
        document.getElementById('linkurl').placeholder = i18n.linkUrl;
        document.getElementById('linkok').textContent = i18n.apply;
        document.getElementById('linkrm').textContent = i18n.remove;
        break;
      }
    }
  }

  window.chrome.webview.addEventListener('message', function(e){
    var m = e.data;
    if (typeof m === 'string'){ try { m = JSON.parse(m); } catch(err){ return; } }
    if (m && m.cmd) handle(m);
  });

  // -- boot ----------------------------------------------------------------------------
  makePage();
  reportPages();
  post({ type:'ready' });
})();
</script>
</body>
</html>
""";
    }
}
