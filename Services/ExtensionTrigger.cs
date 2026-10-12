using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Avalanche.Services;

/// <summary>
/// The click trigger (v1.24.0, re-armed in v1.25.0). A popup-less
/// extension's behavior lives in listeners the extension registers on
/// chrome.action.onClicked inside its own service worker, and the
/// WebView2 SDK lends the host no way to fire that event - the managed
/// surface is add/list/enable/remove, full stop. So the trigger rides
/// inside the extension. Arming a folder wraps the declared background
/// with a shim whose first act is to record every click registration the
/// extension makes, and drops a bridge page the host can open in a
/// hidden window; that page asks the worker, over the extension's own
/// message channel, to run the recorded listeners with the real active
/// tab - the payload Chrome's own toolbar would hand over.
/// Chrome grants activeTab on a genuine toolbar click so the listener
/// may read the page's URL; a synthetic fire cannot carry that grant, so
/// arming adds the standard "tabs" permission instead - the same words
/// the listener would have seen, written into the manifest where the
/// reader can see them. Nothing else about the extension is touched: no
/// externally_connectable, no web_accessible_resources - the bridge
/// speaks same-extension messaging only, and no web page can reach it.
/// v1.25.0's three lessons, learned in the field: the bridge's logic
/// rode in an inline script tag, and every MV3 extension page runs under
/// script-src 'self', so Chromium silenced it before it ever sent a word
/// - the logic now rides in its own file beside the page; the wrapper
/// parked at the extension root, where a classic worker's own relative
/// importScripts would resolve against the wrapper instead of the
/// worker's old neighbors - the wrapper now sits NEXT TO the worker it
/// wraps; and the shim bowed out the moment chrome.action was missing,
/// leaving every manifest-v2 rider (chrome.browserAction) with nobody to
/// answer the bridge - it now arms whichever click event the extension's
/// manifest era speaks, and never lets a refused patch take the worker
/// down with it. The marker moved to v2, and a folder armed by the
/// earlier build upgrades in place: the origin worker is remembered in
/// the marker and confessed by the old wrapper's own import lines, so
/// the upgrade never wraps a wrapper.
/// v1.26.0's lesson: the worker's own tab query could only ever meet
/// the bridge's hidden window - the last-focused window a service
/// worker sees is nobody's toolbar. The host knows which page is open,
/// so the host names it in the bridge's one message; the shim fires
/// the listeners with THAT tab - the profile's real tab whose url
/// matches first, a faithful stand-in built from the host's word
/// second - and the marker moved to v3 so every armed folder picks
/// the new law up in place.
/// </summary>
public static class ExtensionTrigger
{
    public const string MarkerFile = "avalanche_trigger.json";
    public const string BridgePage = "avalanche_bridge.html";
    /// <summary>The bridge's logic, in its own file: an extension page
    /// runs under script-src 'self', which silences every inline script
    /// - v1.24.0's bridge never said a word for exactly that reason.</summary>
    public const string BridgeScriptFile = "avalanche_bridge.js";
    public const string ShimFile = "avalanche_trigger_shim.js";
    public const string ShimModuleFile = "avalanche_trigger_shim.mjs";
    public const string WrapperFile = "avalanche_trigger_sw.js";
    /// <summary>The message tag the bridge page sends and the shim answers.</summary>
    public const string MessageTag = "avalanche-fire-action";
    public const int MarkerVersion = 3;

    private const string TabsPermission = "tabs";

    /// <summary>The folder's background, read the way the loader will meet it.</summary>
    public sealed record TriggerPlan(
        int ManifestVersion,
        string ActionKey,
        bool HasPopup,
        string WorkerPath,
        string WorkerType,
        IReadOnlyList<string> BackgroundScripts,
        string BackgroundPage,
        bool Eligible);

    private static readonly IReadOnlyList<string> NoScripts = Array.Empty<string>();

    /// <summary>True when this folder already rides armed: the marker stands
    /// at the current law, the armed files are in place, and the manifest
    /// itself points at our wrapper with the shim in front - the self-check
    /// heals a folder edited behind our back.</summary>
    public static bool IsArmed(string dir)
    {
        try
        {
            if (!File.Exists(ExtPath(dir, MarkerFile))) return false;
            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(ExtPath(dir, MarkerFile)));
            int v = doc.RootElement.TryGetProperty("v", out JsonElement e)
                && e.TryGetInt32(out int n) ? n : 0;
            if (v < MarkerVersion) return false;
            if (!File.Exists(ExtPath(dir, ShimFile))
                || !File.Exists(ExtPath(dir, BridgePage))
                || !File.Exists(ExtPath(dir, BridgeScriptFile)))
                return false;
            using JsonDocument man = JsonDocument.Parse(
                File.ReadAllText(ExtPath(dir, "manifest.json")));
            JsonElement root = man.RootElement;
            if (!root.TryGetProperty("background", out JsonElement bg)
                || bg.ValueKind != JsonValueKind.Object) return false;
            if (bg.TryGetProperty("service_worker", out JsonElement sw)
                && sw.ValueKind == JsonValueKind.String)
            {
                string pointer = NormalizeRel(sw.GetString() ?? "");
                return FileNameOf(pointer).Equals(WrapperFile, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(ExtPath(dir, pointer));
            }
            if (bg.TryGetProperty("scripts", out JsonElement scripts)
                && scripts.ValueKind == JsonValueKind.Array
                && scripts.GetArrayLength() > 0
                && scripts[0].ValueKind == JsonValueKind.String)
                return scripts[0].GetString() == ShimFile;
            if (bg.TryGetProperty("page", out JsonElement page)
                && page.ValueKind == JsonValueKind.String)
            {
                string html = File.ReadAllText(
                    ExtPath(dir, page.GetString() ?? ""));
                return html.Contains(ShimFile, StringComparison.Ordinal);
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Arms the folder if it qualifies and is not armed already.
    /// Returns true only when something changed this call - the loader uses
    /// that to refresh a copy the profile may still hold from an earlier
    /// run. Every path is idempotent: an armed folder is read, never
    /// rewritten, and a folder armed by an earlier build upgrades in place.</summary>
    public static bool EnsureArmed(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        if (IsArmed(dir)) return false;
        string manifestPath = ExtPath(dir, "manifest.json");
        if (!File.Exists(manifestPath)) return false;
        TriggerPlan plan = ReadPlan(dir);
        if (!plan.Eligible) return false;

        // every armed file is written whole: a marker from an earlier build
        // upgrades in place, and a folder the disk half-ate heals itself
        WriteUtf8(dir, ShimFile, ShimScript);
        WriteUtf8(dir, ShimModuleFile, ShimScript);
        WriteUtf8(dir, BridgePage, BridgeHtml);
        WriteUtf8(dir, BridgeScriptFile, BridgeJs);

        JsonNode? root = JsonNode.Parse(File.ReadAllText(manifestPath));
        if (root is not JsonObject obj) return false;

        string kind = "";          // the marker's word for what was armed
        string origin = "";        // the background the extension declared
        string wrapperPath = "";   // where the wrapper landed (worker kind)
        List<string> chain = new();
        bool changed = false;

        if (plan.ManifestVersion >= 3 && plan.WorkerPath.Length > 0)
        {
            // the worker becomes a wrapper: the shim speaks first, so every
            // listener the original registers lands in the record.
            bool module = string.Equals(plan.WorkerType, "module", StringComparison.Ordinal);
            string pointer = NormalizeRel(plan.WorkerPath);
            bool ours = FileNameOf(pointer).Equals(WrapperFile, StringComparison.OrdinalIgnoreCase);
            if (ours)
            {
                // the manifest already wears our wrapper (a folder armed by
                // an earlier build): the original worker hides behind it -
                // in the marker's memory first, in the old wrapper's own
                // import lines second. Recovering nothing means re-wrapping
                // a wrapper, which is how a worker dies; refuse instead.
                origin = RecoverOrigin(dir);
                if (origin.Length == 0) return false;
            }
            else
            {
                origin = pointer;
            }
            if (origin.Length == 0
                || FileNameOf(origin).Equals(WrapperFile, StringComparison.OrdinalIgnoreCase))
                return false;

            // the wrapper sits NEXT TO the worker it wraps: a classic
            // worker's own relative importScripts resolve against the
            // worker's base URL, and a wrapper parked at the root would
            // tear them away from their neighbors.
            int slash = origin.LastIndexOf('/');
            string wrapperDir = slash >= 0 ? origin[..slash] : "";
            wrapperPath = wrapperDir.Length == 0
                ? WrapperFile
                : wrapperDir + "/" + WrapperFile;
            string shimRef = RefFrom(wrapperDir, module ? ShimModuleFile : ShimFile, module);
            string workerRef = RefSibling(origin, module);
            string wrapper = module
                ? $"import '{shimRef}';\nimport '{workerRef}';\n"
                : $"importScripts('{shimRef}');\nimportScripts('{workerRef}');\n";
            WriteUtf8(dir, wrapperPath, wrapper);
            if (!string.Equals(pointer, wrapperPath, StringComparison.Ordinal))
                obj["background"]!["service_worker"] = wrapperPath;
            obj["background"]!["type"] = module ? "module" : "classic";
            // a wrapper that moved beside its worker leaves the old root seat empty
            if (!string.Equals(wrapperPath, WrapperFile, StringComparison.Ordinal)
                && File.Exists(ExtPath(dir, WrapperFile)))
            {
                try { File.Delete(ExtPath(dir, WrapperFile)); } catch { }
            }
            kind = module ? "module" : "worker";
            changed = true;
        }
        else if (plan.BackgroundScripts.Count > 0)
        {
            // MV2 script chain: the shim joins at the front, where it meets
            // every listener registration that follows. A chain already ours
            // (an earlier build's arming) keeps its tail as the origin.
            bool ours = FileNameOf(NormalizeRel(plan.BackgroundScripts[0]))
                .Equals(ShimFile, StringComparison.OrdinalIgnoreCase);
            for (int i = ours ? 1 : 0; i < plan.BackgroundScripts.Count; i++)
                chain.Add(NormalizeRel(plan.BackgroundScripts[i]));
            if (chain.Count == 0) return false;
            JsonArray scripts = new();
            scripts.Add(ShimFile);
            foreach (string s in chain) scripts.Add(s);
            obj["background"]!["scripts"] = scripts;
            origin = string.Join(", ", chain);
            kind = "scripts";
            changed = true;
        }
        else if (plan.BackgroundPage.Length > 0)
        {
            // MV2 background page: the shim tag is stitched in at the top of
            // the head, before any script the page already loads.
            origin = NormalizeRel(plan.BackgroundPage);
            string pagePath = ExtPath(dir, origin);
            if (!File.Exists(pagePath)) return false;
            string html = File.ReadAllText(pagePath);
            if (!html.Contains(ShimFile, StringComparison.Ordinal))
            {
                string tag = $"<script src=\"{ShimFile}\"></script>";
                Match head = Regex.Match(html, "<head[^>]*>", RegexOptions.IgnoreCase);
                html = head.Success
                    ? html.Insert(head.Index + head.Length, tag)
                    : tag + html;
                File.WriteAllText(pagePath, html, new UTF8Encoding(false));
            }
            kind = "page";
            changed = true;
        }
        if (!changed) return false;

        AddTabsPermission(obj);
        File.WriteAllText(manifestPath,
            obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
            new UTF8Encoding(false));

        JsonObject marker = new()
        {
            ["v"] = MarkerVersion,
            ["armed"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["kind"] = kind,
            ["origin"] = origin,
            ["wrapper"] = wrapperPath,
        };
        File.WriteAllText(ExtPath(dir, MarkerFile),
            marker.ToJsonString(), new UTF8Encoding(false));
        return true;
    }

    /// <summary>Reads the manifest the way Chromium would: which action key
    /// stands, whether a popup answers the click, and what shape the
    /// background takes. Eligible means: a toolbar action without a popup,
    /// and a background we know how to wrap.</summary>
    public static TriggerPlan ReadPlan(string dir)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(ExtPath(dir, "manifest.json")));
            JsonElement root = doc.RootElement;
            int mv = root.TryGetProperty("manifest_version", out JsonElement m)
                && m.TryGetInt32(out int v) ? v : 3;
            string actionKey = "";
            bool hasPopup = false;
            foreach (string key in mv >= 3
                ? new[] { "action", "browser_action" }
                : new[] { "browser_action", "action" })
            {
                if (!root.TryGetProperty(key, out JsonElement a)
                    || a.ValueKind != JsonValueKind.Object) continue;
                actionKey = key;
                hasPopup = a.TryGetProperty("default_popup", out JsonElement p)
                    && p.ValueKind == JsonValueKind.String
                    && (p.GetString() ?? "").Length > 0;
                break;
            }
            string worker = "", workerType = "", bgPage = "";
            List<string> bgScripts = new();
            if (root.TryGetProperty("background", out JsonElement bg)
                && bg.ValueKind == JsonValueKind.Object)
            {
                if (bg.TryGetProperty("service_worker", out JsonElement sw)
                    && sw.ValueKind == JsonValueKind.String)
                    worker = sw.GetString() ?? "";
                if (bg.TryGetProperty("type", out JsonElement t)
                    && t.ValueKind == JsonValueKind.String)
                    workerType = t.GetString() ?? "";
                if (bg.TryGetProperty("scripts", out JsonElement sc)
                    && sc.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement s in sc.EnumerateArray())
                        if (s.ValueKind == JsonValueKind.String)
                            bgScripts.Add(s.GetString() ?? "");
                if (bg.TryGetProperty("page", out JsonElement pg)
                    && pg.ValueKind == JsonValueKind.String)
                    bgPage = pg.GetString() ?? "";
            }
            bool eligible = actionKey.Length > 0
                && !hasPopup
                && (worker.Length > 0 || bgScripts.Count > 0 || bgPage.Length > 0);
            return new TriggerPlan(mv, actionKey, hasPopup, worker, workerType,
                bgScripts, bgPage, eligible);
        }
        catch
        {
            return new TriggerPlan(3, "", false, "", "", NoScripts, "", false);
        }
    }

    /// <summary>Whether the extension's own code refers to the action click
    /// event - the literal property chain survives every bundler, since API
    /// object members cannot be renamed. Answers the toolbar's row: a
    /// popup-less extension with neither an options page nor a click
    /// listener has no surface and rides hidden, as always.</summary>
    public static bool RefersToActionClick(string dir)
    {
        try
        {
            int budget = 32 * 1024 * 1024;
            int files = 0;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".js" && ext != ".mjs") continue;
                string name = Path.GetFileName(file);
                if (name.Equals(ShimFile, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(ShimModuleFile, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(WrapperFile, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(BridgeScriptFile, StringComparison.OrdinalIgnoreCase)) continue;
                if (++files > 400) break;
                FileInfo info = new(file);
                if (info.Length > 4 * 1024 * 1024) continue;
                budget -= (int)info.Length;
                if (budget < 0) break;
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }
                if (text.Contains("chrome.action.onClicked", StringComparison.Ordinal)
                    || text.Contains("browser.action.onClicked", StringComparison.Ordinal))
                    return true;
            }
        }
        catch { /* a folder that will not read hides its rows, as before */ }
        return false;
    }

    private static void AddTabsPermission(JsonObject root)
    {
        if (root["permissions"] is JsonArray perms)
        {
            foreach (JsonNode? p in perms)
                if (p is JsonValue v && v.TryGetValue<string>(out string? s)
                    && string.Equals(s, TabsPermission, StringComparison.Ordinal))
                    return;
            perms.Add(TabsPermission);
        }
        else
        {
            root["permissions"] = new JsonArray(TabsPermission);
        }
    }

    /// <summary>A path under the extension folder, separator-honest: the
    /// manifest's own backslashes become slashes, a leading slash or ./ dies.</summary>
    private static string ExtPath(string dir, string rel)
    {
        string clean = NormalizeRel(rel).Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(dir, clean);
    }

    private static string NormalizeRel(string path)
    {
        string p = (path ?? "").Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.TrimStart('/');
    }

    private static string FileNameOf(string rel)
    {
        int slash = rel.LastIndexOf('/');
        return slash >= 0 ? rel[(slash + 1)..] : rel;
    }

    /// <summary>How a wrapper sitting in <paramref name="wrapperDir"/> (""
    /// meaning the extension root) names one of our files at the root.
    /// Modules must wear the ./ of a relative specifier; classic
    /// importScripts is happy bare.</summary>
    private static string RefFrom(string wrapperDir, string file, bool module)
    {
        string name = FileNameOf(file);
        if (wrapperDir.Length == 0) return module ? "./" + name : name;
        string up = "../";
        int slash = wrapperDir.IndexOf('/');
        while (slash >= 0) { up += "../"; slash = wrapperDir.IndexOf('/', slash + 1); }
        return up + name;
    }

    /// <summary>How the wrapper names the worker it wraps - the two share a
    /// directory by design.</summary>
    private static string RefSibling(string origin, bool module)
    {
        string name = FileNameOf(origin);
        return module ? "./" + name : name;
    }

    /// <summary>The original worker behind an earlier build's wrapper: the
    /// marker remembers it first; otherwise the old wrapper's own import
    /// lines confess it - the last import that is not one of ours.</summary>
    private static string RecoverOrigin(string dir)
    {
        try
        {
            string markerPath = ExtPath(dir, MarkerFile);
            if (File.Exists(markerPath))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(markerPath));
                if (doc.RootElement.TryGetProperty("origin", out JsonElement o)
                    && o.ValueKind == JsonValueKind.String)
                {
                    string fromMarker = NormalizeRel(o.GetString() ?? "");
                    if (fromMarker.Length > 0
                        && !FileNameOf(fromMarker).Equals(WrapperFile, StringComparison.OrdinalIgnoreCase))
                        return fromMarker;
                }
            }
        }
        catch { /* the marker's memory is best-effort */ }
        try
        {
            string wrapperPath = ExtPath(dir, WrapperFile);
            if (!File.Exists(wrapperPath)) return "";
            string wrapper = File.ReadAllText(wrapperPath);
            MatchCollection matches = Regex.Matches(wrapper,
                "importScripts\\(\\s*['\"]([^'\"]+)['\"]\\s*\\)|import\\s+['\"]([^'\"]+)['\"]");
            string found = "";
            foreach (Match m in matches)
            {
                string candidate = NormalizeRel(
                    m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                string name = FileNameOf(candidate);
                if (name.Equals(ShimFile, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(ShimModuleFile, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(WrapperFile, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (candidate.Length > 0) found = candidate;
            }
            return found;
        }
        catch { return ""; }
    }

    private static void WriteUtf8(string dir, string name, string content)
        => File.WriteAllText(ExtPath(dir, name), content, new UTF8Encoding(false));

    /// <summary>The shim: records every click registration (and honors its
    /// removeListener), then answers the bridge's one message by firing the
    /// record with the tab Chrome's own toolbar would have handed over -
    /// the host names the open page in that message, and the worker's
    /// own tab query serves only when no name came. Both click events
    /// of both manifest eras
    /// are armed, every swap is guarded so a refused patch cannot take the
    /// worker down, and the onMessage listener stands at the top of the
    /// worker regardless - a message that wakes a sleeping worker finds it.</summary>
        private const string ShimScript = @"// Avalanche click-trigger shim: the host browser armed this folder.
// It records every click listener this extension registers on
// chrome.action.onClicked (or chrome.browserAction.onClicked, for the
// manifest-v2 era) and fires them, with the tab Chrome's own toolbar
// would have handed over, when the host's bridge page asks. The host
// knows which page is open - it names that page in the bridge's one
// message; the shim prefers the real tab whose url matches and
// settles for a faithful stand-in only when the profile keeps no such
// tab, because the bridge's hidden window is nobody's idea of a
// focused one.
(function () {
  'use strict';
  if (typeof chrome === 'undefined' || !chrome.runtime || !chrome.runtime.onMessage) return;
  var listeners = [];
  var arm = function (evt) {
    try {
      var realAdd = evt.addListener.bind(evt);
      evt.addListener = function (fn) {
        listeners.push(fn);
        return realAdd(fn);
      };
      var realRemove = evt.removeListener.bind(evt);
      evt.removeListener = function (fn) {
        var i = listeners.indexOf(fn);
        if (i >= 0) listeners.splice(i, 1);
        return realRemove(fn);
      };
    } catch (e) { /* a sealed event still serves the genuine click */ }
  };
  if (chrome.action && chrome.action.onClicked) arm(chrome.action.onClicked);
  if (chrome.browserAction && chrome.browserAction.onClicked) arm(chrome.browserAction.onClicked);
  // one query, both dialects: the promise the modern runtime returns,
  // or the callback the older pages still speak
  var q = function (opts) {
    return new Promise(function (resolve) {
      try {
        var r = chrome.tabs.query(opts);
        if (r && typeof r.then === 'function') { r.then(resolve, function () { resolve([]); }); return; }
      } catch (e) { resolve([]); return; }
      try { chrome.tabs.query(opts, function (tabs) { resolve(tabs || []); }); }
      catch (e) { resolve([]); }
    });
  };
  var standIn = function (url, title) {
    return {
      id: -1, index: 0, windowId: -1, highlighted: true, active: true,
      pinned: false, incognito: false, selected: true, audible: false,
      autoDiscardable: true, discarded: false, mutedInfo: { muted: false },
      status: 'complete', url: url, title: title || '', favIconUrl: ''
    };
  };
  chrome.runtime.onMessage.addListener(function (msg, sender, sendResponse) {
    if (!msg || msg.type !== 'avalanche-fire-action') return;
    (function () {
      var url = typeof msg.url === 'string' ? msg.url : '';
      var title = typeof msg.title === 'string' ? msg.title : '';
      var tabPromise;
      if (url.length > 0) {
        // the host named the open page: the real tab whose url matches
        // first, the host's own word dressed as a tab second
        tabPromise = q({}).then(function (tabs) {
          for (var i = 0; i < tabs.length; i++) {
            if (tabs[i] && tabs[i].url === url) return tabs[i];
          }
          return standIn(url, title);
        });
      } else {
        tabPromise = q({ active: true, lastFocusedWindow: true }).then(function (tabs) {
          return tabs.length ? tabs[0]
            : q({ active: true, currentWindow: true }).then(function (t2) {
                return t2.length ? t2[0] : standIn('', '');
              });
        });
      }
      tabPromise.then(function (tab) {
        var fired = 0;
        for (var i = 0; i < listeners.length; i++) {
          try { listeners[i](tab); fired++; } catch (e) { /* its own business */ }
        }
        try { sendResponse({ ok: true, fired: fired, listeners: listeners.length }); }
        catch (e) { }
      });
    })();
    return true;   // the answer arrives later
  });
})();
";

    /// <summary>The bridge page: an extension page like any other, opened by
    /// the host in a hidden window. Its script tag is EXTERNAL - an
    /// extension page runs under script-src 'self', which silences every
    /// inline script, and v1.24.0's bridge never said a word for exactly
    /// that reason. No web page can navigate here or send here.</summary>
    private const string BridgeHtml = @"<!DOCTYPE html>
<html>
<head><meta charset=""utf-8""><title>Avalanche</title></head>
<body>
<script src=""avalanche_bridge.js""></script>
</body>
</html>
";

    /// <summary>The bridge's logic, in the file the page's own CSP allows:
    /// it holds the extension's own message rights, asks its worker once,
    /// and reports the answer back through the host channel.</summary>
        private const string BridgeJs = @"// Avalanche bridge: an extension page like any other, opened by the host
// in a hidden window. It holds the extension's own message rights, asks
// its worker once, and reports the answer back through the host channel.
// The logic lives in this file because an extension page's CSP
// (script-src 'self') silences every inline script - the previous
// bridge never said a word for exactly that reason. And the host names
// the page that is open in its own query string: the worker's tab
// query alone would meet only this hidden window, so the open page
// rides to it as avUrl and avTitle.
(function () {
  'use strict';
  var send = function (m) { try { window.chrome.webview.postMessage(m); } catch (e) { } };
  var q = null;
  try { q = new URLSearchParams(location.search); } catch (e) { q = null; }
  var arg = function (k) { try { return (q && q.get(k)) || ''; } catch (e) { return ''; } };
  try {
    chrome.runtime.sendMessage({
      type: 'avalanche-fire-action',
      url: arg('avUrl'),
      title: arg('avTitle')
    }, function (res) {
      var err = chrome.runtime.lastError;
      if (err) { send({ type: 'avalanche-fire-result', ok: false, error: String(err.message || err) }); return; }
      send({
        type: 'avalanche-fire-result',
        ok: !!res && !!res.ok,
        fired: (res && res.fired) || 0,
        listeners: (res && res.listeners) || 0
      });
    });
  } catch (e) {
    send({ type: 'avalanche-fire-result', ok: false, error: String(e && e.message || e) });
  }
})();
";
}
