(function() {
  function check() {
  // Detect PDF pages - Chromium's native viewer AND the embedded/wrapped
  // documents academic publishers serve. Wiley (and its kin) answer the PDF
  // route with an HTML shell that carries the document in an iframe, embed or
  // object, so the PAGE answers text/html while the document is still on it.
  const isPdf = document.contentType === 'application/pdf'
    || window.location.href.startsWith('chrome-extension://mhjfbmdgcfjbbpaeojofohoefgiehjai/')
    || document.querySelector('embed[type="application/pdf"]') !== null
    || document.querySelector('iframe[src$=".pdf"]') !== null
    || document.querySelector('iframe[src*="application/pdf"]') !== null
    || document.querySelector('object[type="application/pdf"]') !== null
    || document.querySelector('object[data$=".pdf"]') !== null;

  // Publisher viewer wrappers: the address itself is pdf-shaped even when the
  // page is an HTML shell around the document - Wiley's /doi/pdf/... among
  // them, plus the getpdf and viewcontent.cgi routes repositories use.
  const urlPath = window.location.pathname.toLowerCase();
  const urlLooksPdf = urlPath.endsWith('.pdf')
    || /\/pdf\//.test(urlPath)
    || /\/doi\/pdf\//.test(urlPath)
    || /getpdf/i.test(urlPath)
    || /viewcontent\.cgi/i.test(urlPath);

  if (!isPdf && !urlLooksPdf) return;
  if (document.getElementById('avalanche-open-btn')) return;

  const btn = document.createElement('button');
  btn.id = 'avalanche-open-btn';
  btn.title = 'Open in Avalanche';
  btn.innerHTML = '<svg viewBox="0 0 24 24" width="28" height="28" fill="white">' +
    '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6z"/>' +
    '<polyline points="14,2 14,8 20,8" fill="none" stroke="white" stroke-width="1.5"/>' +
    '<line x1="9" y1="13" x2="15" y2="13" stroke="rgba(74,144,217,1)" stroke-width="1.5"/>' +
    '<line x1="9" y1="17" x2="13" y2="17" stroke="rgba(74,144,217,1)" stroke-width="1.5"/>' +
    '</svg>';

  btn.addEventListener('click', function(e) {
    e.preventDefault();
    e.stopPropagation();

    // A wrapper page keeps the real document in its own element: prefer the
    // embedded address over the wrapper's, so the download carries the bytes
    // and not the shell.
    var pdfUrl = window.location.href;
    var embed = document.querySelector('embed[type="application/pdf"]');
    var iframe = document.querySelector('iframe[src$=".pdf"]')
              || document.querySelector('iframe[src*="pdf"]');
    var obj = document.querySelector('object[type="application/pdf"]')
           || document.querySelector('object[data$=".pdf"]');

    if (embed && embed.src) pdfUrl = embed.src;
    else if (iframe && iframe.src) pdfUrl = iframe.src;
    else if (obj && (obj.data || obj.getAttribute('data'))) pdfUrl = obj.data || obj.getAttribute('data');

    // Method 1: Try anchor download (triggers OnDownloadStarting)
    try {
      var a = document.createElement('a');
      a.href = pdfUrl;
      a.download = 'document.pdf';
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
    } catch(err) {
      // Method 2: If anchor fails (CSP), use postMessage to C# host
      try {
        window.chrome.webview.postMessage(JSON.stringify({
          type: 'avalanche-open-pdf',
          url: pdfUrl
        }));
      } catch(e2) {}
    }
  });

  document.body.appendChild(btn);
  }

  // Wiley's viewer loads its iframe long after document_idle: the whole check
  // runs again on a delay, so the button appears once the document's shell
  // exists. Re-running is safe - the guard above keeps one button alive.
  setTimeout(check, 2000);
  setTimeout(check, 5000);
})();
