using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Avalanche.Services;

/// <summary>
/// The click trigger (v1.24.0). A popup-less extension's behavior lives in
/// listeners the extension registers on chrome.action.onClicked inside its
/// own service worker, and the WebView2 SDK lends the host no way to fire
/// that event - the managed surface is add/list/enable/remove, full stop.
/// So the trigger rides inside the extension. Arming a folder wraps the
/// declared background with a shim whose first act is to record every
/// onClicked registration the extension makes, and drops a bridge page the
/// host can open in a hidden window; that page asks the worker, over the
/// extension's own message channel, to run the recorded listeners with the
/// real active tab - the payload Chrome's own toolbar would hand over.
/// Chrome grants activeTab on a genuine toolbar click so the listener may
/// read the page's URL; a synthetic fire cannot carry that grant, so arming
/// adds the standard "tabs" permission instead - the same words the
/// listener would have seen, written into the manifest where the reader
/// can see them. Nothing else about the extension is touched: no
/// externally_connectable, no web_accessible_resources - the bridge speaks
/// same-extension messaging only, and no web page can reach it.
/// </summary>
public static class ExtensionTrigger
{
    public const string MarkerFile = "avalanche_trigger.json";
    public const string BridgePage = "avalanche_bridge.html";
    public const string ShimFile = "avalanche_trigger_shim.js";
    public const string ShimModuleFile = "avalanche_trigger_shim.mjs";
    public const string WrapperFile = "avalanche_trigger_sw.js";
    /// <summary>The message tag the bridge page sends and the shim answers.</summary>
    public const string MessageTag = "avalanche-fire-action";
    public const int MarkerVersion = 1;

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

    /// <summary>True when this folder already rides armed: the marker stands,
    /// and the manifest itself points at our wrapper with every armed file
    /// in place - the self-check heals a folder edited behind our back.</summary>
    public static bool IsArmed(string dir)
    {
        try
        {
            if (!File.Exists(Path.Combine(dir, MarkerFile))) return false;
            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(dir, MarkerFile)));
            int v = doc.RootElement.TryGetProperty("v", out JsonElement e)
                && e.TryGetInt32(out int n) ? n : 0;
            if (v < MarkerVersion) return false;
            if (!File.Exists(Path.Combine(dir, ShimFile))
                || !File.Exists(Path.Combine(dir, BridgePage)))
                return false;
            using JsonDocument man = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(dir, "manifest.json")));
            JsonElement root = man.RootElement;
            if (!root.TryGetProperty("background", out JsonElement bg)
                || bg.ValueKind != JsonValueKind.Object) return false;
            if (bg.TryGetProperty("service_worker", out JsonElement sw)
                && sw.ValueKind == JsonValueKind.String)
                return sw.GetString() == WrapperFile
                    && File.Exists(Path.Combine(dir, WrapperFile));
            if (bg.TryGetProperty("scripts", out JsonElement scripts)
                && scripts.ValueKind == JsonValueKind.Array
                && scripts.GetArrayLength() > 0
                && scripts[0].ValueKind == JsonValueKind.String)
                return scripts[0].GetString() == ShimFile;
            if (bg.TryGetProperty("page", out JsonElement page)
                && page.ValueKind == JsonValueKind.String)
            {
                string html = File.ReadAllText(
                    Path.Combine(dir, page.GetString() ?? ""));
                return html.Contains(ShimFile, StringComparison.Ordinal);
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Arms the folder if it qualifies and is not armed already.
    /// Returns true only when files changed this call - the loader uses that
    /// to refresh a copy the profile may still hold from an earlier run.
    /// Every path is idempotent: an armed folder is read, never rewritten.</summary>
    public static bool EnsureArmed(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        if (IsArmed(dir)) return false;
        string manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath)) return false;
        TriggerPlan plan = ReadPlan(dir);
        if (!plan.Eligible) return false;

        WriteUtf8(dir, ShimFile, ShimScript);
        WriteUtf8(dir, ShimModuleFile, ShimScript);
        WriteUtf8(dir, BridgePage, BridgeHtml);

        JsonNode? root = JsonNode.Parse(File.ReadAllText(manifestPath));
        if (root is not JsonObject obj) return false;

        bool changed = false;
        if (plan.ManifestVersion >= 3 && plan.WorkerPath.Length > 0)
        {
            // the worker becomes a wrapper: the shim speaks first, so every
            // listener the original registers lands in the record.
            bool module = string.Equals(plan.WorkerType, "module", StringComparison.Ordinal);
            string origin = plan.WorkerPath.Replace('\\', '/');
            string wrapper = module
                ? $"import './{ShimModuleFile}';\nimport './{origin}';\n"
                : $"importScripts('{ShimFile}');\nimportScripts('{origin}');\n";
            WriteUtf8(dir, WrapperFile, wrapper);
            obj["background"]!["service_worker"] = WrapperFile;
            obj["background"]!["type"] = module ? "module" : "classic";
            changed = true;
        }
        else if (plan.BackgroundScripts.Count > 0)
        {
            // MV2 script chain: the shim joins at the front, where it meets
            // every listener registration that follows.
            JsonArray scripts = new();
            scripts.Add(ShimFile);
            foreach (string s in plan.BackgroundScripts) scripts.Add(s);
            obj["background"]!["scripts"] = scripts;
            changed = true;
        }
        else if (plan.BackgroundPage.Length > 0)
        {
            // MV2 background page: the shim tag is stitched in at the top of
            // the head, before any script the page already loads.
            string pagePath = Path.Combine(dir,
                plan.BackgroundPage.Replace('/', Path.DirectorySeparatorChar));
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
        };
        File.WriteAllText(Path.Combine(dir, MarkerFile),
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
                File.ReadAllText(Path.Combine(dir, "manifest.json")));
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
                    || name.Equals(WrapperFile, StringComparison.OrdinalIgnoreCase)) continue;
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

    private static void WriteUtf8(string dir, string name, string content)
        => File.WriteAllText(Path.Combine(dir, name), content, new UTF8Encoding(false));

    /// <summary>The shim: records every onClicked registration (and honors
    /// its removeListener), then answers the bridge's one message by firing
    /// the record with the real active tab - the tab query a toolbar click
    /// would have answered with. The onMessage listener stands at the top of
    /// the worker, so a message that wakes a sleeping worker finds it.</summary>
    private const string ShimScript = @"// Avalanche click-trigger shim: the host browser armed this folder.
// It records every chrome.action.onClicked listener this extension
// registers and fires them, with the real active tab, when the host's
// bridge page asks - the click Chrome's own toolbar would have delivered.
(function () {
  'use strict';
  if (typeof chrome === 'undefined' || !chrome.action || !chrome.action.onClicked) return;
  var listeners = [];
  var realAdd = chrome.action.onClicked.addListener.bind(chrome.action.onClicked);
  chrome.action.onClicked.addListener = function (fn) {
    listeners.push(fn);
    return realAdd(fn);
  };
  var realRemove = chrome.action.onClicked.removeListener.bind(chrome.action.onClicked);
  chrome.action.onClicked.removeListener = function (fn) {
    var i = listeners.indexOf(fn);
    if (i >= 0) listeners.splice(i, 1);
    return realRemove(fn);
  };
  chrome.runtime.onMessage.addListener(function (msg, sender, sendResponse) {
    if (!msg || msg.type !== 'avalanche-fire-action') return;
    (function () {
      var pick = function (tabs) { return (tabs && tabs.length) ? tabs[0] : null; };
      var tabPromise;
      try {
        tabPromise = chrome.tabs.query({ active: true, lastFocusedWindow: true })
          .then(pick)
          .then(function (t) {
            return t || chrome.tabs.query({ active: true, currentWindow: true }).then(pick);
          })
          .catch(function () { return null; });
      } catch (e) { tabPromise = Promise.resolve(null); }
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
    /// the host in a hidden window. It holds the extension's own message
    /// rights, asks its worker once, and reports the answer back through the
    /// host channel. No web page can navigate here or send here.</summary>
    private const string BridgeHtml = @"<!DOCTYPE html>
<html>
<head><meta charset=""utf-8""><title>Avalanche</title></head>
<body>
<script>
(function () {
  'use strict';
  var send = function (m) { try { window.chrome.webview.postMessage(m); } catch (e) { } };
  try {
    chrome.runtime.sendMessage({ type: 'avalanche-fire-action' }, function (res) {
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
</script>
</body>
</html>
";
}
