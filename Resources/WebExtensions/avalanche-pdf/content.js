(function() {
  // Detect Chromium's PDF viewer:
  // 1. document.contentType is 'application/pdf'
  // 2. We're inside the PDF viewer extension
  // 3. There's an embed[type="application/pdf"] on the page
  const isPdf = document.contentType === 'application/pdf'
    || window.location.href.startsWith('chrome-extension://mhjfbmdgcfjbbpaeojofohoefgiehjai/')
    || document.querySelector('embed[type="application/pdf"]') !== null;

  if (!isPdf) return;
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
    // Method 1: Try anchor download (triggers OnDownloadStarting)
    try {
      var a = document.createElement('a');
      a.href = window.location.href;
      a.download = 'document.pdf';
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
    } catch(err) {
      // Method 2: If anchor fails (CSP), use postMessage to C# host
      try {
        window.chrome.webview.postMessage(JSON.stringify({
          type: 'avalanche-open-pdf',
          url: window.location.href
        }));
      } catch(e2) {}
    }
  });

  document.body.appendChild(btn);
})();
