using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalanche.Services;
using Xunit;

namespace Avalanche.Tests;

/// <summary>The trigger's arming law: a popup-less action with a background
/// we can wrap is armed exactly once - the manifest ends up pointing at the
/// wrapper, every armed file stands, "tabs" joins the permissions for the
/// URL parity Chrome's activeTab grant would have carried - and a second
/// pass is a no-op. Popup-ful, actionless and backgroundless folders are
/// never touched.</summary>
public sealed class ExtensionTriggerTests
{
    private static string WriteDir(params (string Path, string Content)[] files)
    {
        string root = Path.Combine(Path.GetTempPath(),
            "avalanche-trig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (path, content) in files)
        {
            string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return root;
    }

    private static JsonElement ManifestOf(string dir)
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir, "manifest.json")));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Mv3_Classic_Worker_Is_Wrapped_And_Armed()
    {
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{\"default_icon\":{\"16\":\"i.png\"}},"
                + "\"background\":{\"service_worker\":\"sw.js\"},"
                + "\"permissions\":[\"storage\"],"
                + "\"options_ui\":{\"options_page\":\"opts.html\"}}"),
            ("sw.js", "chrome.action.onClicked.addListener(tab => console.log(tab.url));"));
        try
        {
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            JsonElement man = ManifestOf(dir);
            Assert.Equal(ExtensionTrigger.WrapperFile,
                man.GetProperty("background").GetProperty("service_worker").GetString());
            Assert.Equal("classic",
                man.GetProperty("background").GetProperty("type").GetString());
            string wrapper = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.WrapperFile));
            Assert.Contains("importScripts('" + ExtensionTrigger.ShimFile + "')", wrapper);
            Assert.Contains("importScripts('sw.js')", wrapper);
            Assert.True(File.Exists(Path.Combine(dir, ExtensionTrigger.ShimFile)));
            Assert.True(File.Exists(Path.Combine(dir, ExtensionTrigger.ShimModuleFile)));
            Assert.True(File.Exists(Path.Combine(dir, ExtensionTrigger.BridgePage)));
            Assert.True(File.Exists(Path.Combine(dir, ExtensionTrigger.BridgeScriptFile)));
            Assert.True(File.Exists(Path.Combine(dir, ExtensionTrigger.MarkerFile)));
            var perms = man.GetProperty("permissions").EnumerateArray()
                .Select(p => p.GetString()).ToList();
            Assert.Contains("storage", perms);
            Assert.Contains("tabs", perms);
            // nothing else moved
            Assert.Equal("T", man.GetProperty("name").GetString());
            Assert.Equal("opts.html",
                man.GetProperty("options_ui").GetProperty("options_page").GetString());
            Assert.Equal("chrome.action.onClicked.addListener(tab => console.log(tab.url));",
                File.ReadAllText(Path.Combine(dir, "sw.js")));
            Assert.True(ExtensionTrigger.IsArmed(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Second_Pass_Is_A_NoOp()
    {
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{},\"background\":{\"service_worker\":\"sw.js\"}}"),
            ("sw.js", "chrome.action.onClicked.addListener(() => {});"));
        try
        {
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            string marker = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.MarkerFile));
            Assert.False(ExtensionTrigger.EnsureArmed(dir));
            Assert.Equal(marker, File.ReadAllText(Path.Combine(dir, ExtensionTrigger.MarkerFile)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Mv3_Module_Worker_Keeps_Module_Type()
    {
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{},"
                + "\"background\":{\"service_worker\":\"sw.js\",\"type\":\"module\"}}"),
            ("sw.js", "chrome.action.onClicked.addListener(() => {});"));
        try
        {
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            JsonElement man = ManifestOf(dir);
            Assert.Equal("module",
                man.GetProperty("background").GetProperty("type").GetString());
            string wrapper = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.WrapperFile));
            Assert.Contains($"import './{ExtensionTrigger.ShimModuleFile}'", wrapper);
            Assert.Contains("import './sw.js'", wrapper);
            Assert.DoesNotContain("importScripts", wrapper);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Mv2_Script_Chain_Gets_The_Shim_First()
    {
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":2,\"name\":\"T\",\"version\":\"1.0\","
                + "\"browser_action\":{},"
                + "\"background\":{\"scripts\":[\"a.js\",\"b.js\"],\"persistent\":false}}"),
            ("a.js", "chrome.browserAction.onClicked.addListener(() => {});"),
            ("b.js", ""));
        try
        {
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            JsonElement bg = ManifestOf(dir).GetProperty("background");
            var scripts = bg.GetProperty("scripts").EnumerateArray()
                .Select(s => s.GetString()).ToList();
            Assert.Equal(new[] { ExtensionTrigger.ShimFile, "a.js", "b.js" }, scripts);
            Assert.Equal("false", bg.GetProperty("persistent").GetRawText());
            Assert.True(ExtensionTrigger.IsArmed(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Mv2_Background_Page_Gets_The_Shim_Tag()
    {
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":2,\"name\":\"T\",\"version\":\"1.0\","
                + "\"browser_action\":{},\"background\":{\"page\":\"bg.html\"}}"),
            ("bg.html", "<html><head><meta charset=\"utf-8\"></head>"
                + "<body><script src=\"bg.js\"></script></body></html>"),
            ("bg.js", "chrome.browserAction.onClicked.addListener(() => {});"));
        try
        {
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            string html = File.ReadAllText(Path.Combine(dir, "bg.html"));
            string tag = $"<script src=\"{ExtensionTrigger.ShimFile}\"></script>";
            int headEnd = html.IndexOf("<head>", StringComparison.Ordinal) + "<head>".Length;
            Assert.True(html.Contains(tag));
            Assert.Equal(headEnd, html.IndexOf(tag, StringComparison.Ordinal));
            Assert.True(ExtensionTrigger.IsArmed(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PopupFul_Folder_Is_Never_Touched()
    {
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{\"default_popup\":\"p.html\"},"
                + "\"background\":{\"service_worker\":\"sw.js\"}}"),
            ("sw.js", "chrome.action.onClicked.addListener(() => {});"));
        try
        {
            Assert.False(ExtensionTrigger.EnsureArmed(dir));
            Assert.False(File.Exists(Path.Combine(dir, ExtensionTrigger.MarkerFile)));
            Assert.False(File.Exists(Path.Combine(dir, ExtensionTrigger.ShimFile)));
            Assert.Equal("{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{\"default_popup\":\"p.html\"},"
                + "\"background\":{\"service_worker\":\"sw.js\"}}",
                File.ReadAllText(Path.Combine(dir, "manifest.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Actionless_And_Backgroundless_Folders_Are_Never_Touched()
    {
        string noAction = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"background\":{\"service_worker\":\"sw.js\"}}"),
            ("sw.js", "chrome.runtime.onInstalled.addListener(() => {});"));
        string noBackground = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{}}"));
        try
        {
            Assert.False(ExtensionTrigger.EnsureArmed(noAction));
            Assert.False(ExtensionTrigger.EnsureArmed(noBackground));
            Assert.False(File.Exists(Path.Combine(noAction, ExtensionTrigger.MarkerFile)));
            Assert.False(File.Exists(Path.Combine(noBackground, ExtensionTrigger.MarkerFile)));
        }
        finally
        {
            Directory.Delete(noAction, recursive: true);
            Directory.Delete(noBackground, recursive: true);
        }
    }

    [Fact]
    public void IsArmed_Notices_A_Missing_Wrapper()
    {
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{},\"background\":{\"service_worker\":\"sw.js\"}}"),
            ("sw.js", "chrome.action.onClicked.addListener(() => {});"));
        try
        {
            Assert.False(ExtensionTrigger.IsArmed(dir));
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            Assert.True(ExtensionTrigger.IsArmed(dir));
            File.Delete(Path.Combine(dir, ExtensionTrigger.WrapperFile));
            Assert.False(ExtensionTrigger.IsArmed(dir));
            // and the self-check heals the folder on the next pass
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            Assert.True(ExtensionTrigger.IsArmed(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Click_Listener_Scan_Reads_The_Workers_Own_Words()
    {
        string hit = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\"}"),
            ("bg/worker.js", "browser.action.onClicked.addListener(() => {});"));
        string clean = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\"}"),
            ("bg/worker.js", "chrome.runtime.onInstalled.addListener(() => {});"));
        try
        {
            Assert.True(ExtensionTrigger.RefersToActionClick(hit));
            Assert.False(ExtensionTrigger.RefersToActionClick(clean));
        }
        finally
        {
            Directory.Delete(hit, recursive: true);
            Directory.Delete(clean, recursive: true);
        }
    }

    [Fact]
    public void ReadPlan_Says_What_Stands()
    {
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{},\"background\":{\"service_worker\":\"sw.js\"}}"),
            ("sw.js", ""));
        try
        {
            ExtensionTrigger.TriggerPlan plan = ExtensionTrigger.ReadPlan(dir);
            Assert.Equal(3, plan.ManifestVersion);
            Assert.Equal("action", plan.ActionKey);
            Assert.False(plan.HasPopup);
            Assert.Equal("sw.js", plan.WorkerPath);
            Assert.True(plan.Eligible);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Bridge_Page_Is_Csp_Safe_For_Mv3()
    {
        // every MV3 extension page runs under script-src 'self' - an inline
        // script would never run, and v1.24.0's trigger died in exactly
        // that silence. The logic rides in its own file now, and the page
        // wears nothing but the tag that names it.
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{},\"background\":{\"service_worker\":\"sw.js\"}}"),
            ("sw.js", "chrome.action.onClicked.addListener(() => {});"));
        try
        {
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            string html = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.BridgePage));
            Assert.Contains("<script src=\"" + ExtensionTrigger.BridgeScriptFile + "\"></script>", html);
            Assert.DoesNotContain("<script>", html);
            string js = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.BridgeScriptFile));
            Assert.Contains(ExtensionTrigger.MessageTag, js);
            Assert.Contains("postMessage", js);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Shim_Covers_The_Mv2_Browser_Action_Too()
    {
        // the shim once bowed out the moment chrome.action was missing,
        // leaving every manifest-v2 rider with nobody to answer the bridge
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":2,\"name\":\"T\",\"version\":\"1.0\","
                + "\"browser_action\":{},\"background\":{\"scripts\":[\"a.js\"],\"persistent\":false}}"),
            ("a.js", "chrome.browserAction.onClicked.addListener(() => {});"));
        try
        {
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            string shim = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.ShimFile));
            Assert.Contains("browserAction", shim);
            Assert.Contains("chrome.runtime.onMessage.addListener", shim);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Mv3_Worker_In_A_Subdirectory_Wraps_Beside_The_Worker()
    {
        // a classic worker's own relative importScripts resolve against the
        // worker's base URL - the wrapper must share its directory, or the
        // worker's neighbors are torn away from it
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{},\"background\":{\"service_worker\":\"bg/worker.js\"}}"),
            ("bg/worker.js", "importScripts('helper.js');"
                + "chrome.action.onClicked.addListener(() => {});"),
            ("bg/helper.js", ""));
        try
        {
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            JsonElement man = ManifestOf(dir);
            Assert.Equal("bg/" + ExtensionTrigger.WrapperFile,
                man.GetProperty("background").GetProperty("service_worker").GetString());
            string wrapper = File.ReadAllText(
                Path.Combine(dir, "bg", ExtensionTrigger.WrapperFile));
            Assert.Contains("importScripts('../avalanche_trigger_shim.js')", wrapper);
            Assert.Contains("importScripts('worker.js')", wrapper);
            Assert.True(File.Exists(Path.Combine(dir, ExtensionTrigger.ShimFile)));
            Assert.True(ExtensionTrigger.IsArmed(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Module_Worker_In_A_Subdirectory_Keeps_Relative_Specifiers()
    {
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{},\"background\":{\"service_worker\":\"bg/worker.js\",\"type\":\"module\"}}"),
            ("bg/worker.js", "import './dep.js';"
                + "chrome.action.onClicked.addListener(() => {});"),
            ("bg/dep.js", "export const x = 1;"));
        try
        {
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            JsonElement man = ManifestOf(dir);
            Assert.Equal("bg/" + ExtensionTrigger.WrapperFile,
                man.GetProperty("background").GetProperty("service_worker").GetString());
            Assert.Equal("module",
                man.GetProperty("background").GetProperty("type").GetString());
            string wrapper = File.ReadAllText(
                Path.Combine(dir, "bg", ExtensionTrigger.WrapperFile));
            Assert.Contains("import '../avalanche_trigger_shim.mjs';", wrapper);
            Assert.Contains("import './worker.js';", wrapper);
            Assert.DoesNotContain("importScripts", wrapper);
            Assert.True(ExtensionTrigger.IsArmed(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void V1_Armed_Folder_Upgrades_Without_Wrapping_The_Wrapper()
    {
        // a folder armed by the earlier build: the manifest already wears
        // the wrapper, the marker stands at v1, the bridge holds an inline
        // script that no MV3 page was ever allowed to run. The upgrade
        // recovers the origin worker and rebuilds - it must never point
        // the wrapper at itself.
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{},\"background\":{\"service_worker\":\"avalanche_trigger_sw.js\",\"type\":\"classic\"},"
                + "\"permissions\":[\"tabs\"]}"),
            ("avalanche_trigger_sw.js",
                "importScripts('avalanche_trigger_shim.js');\nimportScripts('sw.js');\n"),
            ("sw.js", "chrome.action.onClicked.addListener(() => {});"),
            ("avalanche_trigger_shim.js", "/* the old shim's seat */"),
            ("avalanche_bridge.html",
                "<!DOCTYPE html><html><body><script>OLD INLINE WORD</script></body></html>"),
            ("avalanche_trigger.json", "{\"v\":1,\"armed\":\"2026-10-11T00:00:00Z\"}"));
        try
        {
            Assert.False(ExtensionTrigger.IsArmed(dir));   // a v1 marker no longer counts
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            string wrapper = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.WrapperFile));
            Assert.Contains("importScripts('avalanche_trigger_shim.js')", wrapper);
            Assert.Contains("importScripts('sw.js')", wrapper);
            Assert.DoesNotContain("importScripts('avalanche_trigger_sw.js')", wrapper);
            JsonElement man = ManifestOf(dir);
            Assert.Equal(ExtensionTrigger.WrapperFile,
                man.GetProperty("background").GetProperty("service_worker").GetString());
            string html = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.BridgePage));
            Assert.DoesNotContain("OLD INLINE WORD", html);
            Assert.Contains(ExtensionTrigger.BridgeScriptFile, html);
            Assert.True(File.Exists(Path.Combine(dir, ExtensionTrigger.BridgeScriptFile)));
            using JsonDocument marker = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(dir, ExtensionTrigger.MarkerFile)));
            // a v1 folder rides the CURRENT law: the marker lands at v3
            Assert.Equal(3, marker.RootElement.GetProperty("v").GetInt32());
            Assert.Equal("sw.js", marker.RootElement.GetProperty("origin").GetString());
            Assert.True(ExtensionTrigger.IsArmed(dir));
            Assert.False(ExtensionTrigger.EnsureArmed(dir));   // the next pass rests
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void V1_Subdirectory_Wrapper_Moves_Beside_Its_Worker()
    {
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{},\"background\":{\"service_worker\":\"avalanche_trigger_sw.js\",\"type\":\"classic\"}}"),
            ("avalanche_trigger_sw.js",
                "importScripts('avalanche_trigger_shim.js');\nimportScripts('bg/worker.js');\n"),
            ("bg/worker.js", "importScripts('dep.js');"
                + "chrome.action.onClicked.addListener(() => {});"),
            ("bg/dep.js", ""),
            ("avalanche_trigger_shim.js", "/* the old shim's seat */"),
            ("avalanche_bridge.html", "<html></html>"),
            ("avalanche_trigger.json", "{\"v\":1}"));
        try
        {
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            JsonElement man = ManifestOf(dir);
            Assert.Equal("bg/" + ExtensionTrigger.WrapperFile,
                man.GetProperty("background").GetProperty("service_worker").GetString());
            string wrapper = File.ReadAllText(
                Path.Combine(dir, "bg", ExtensionTrigger.WrapperFile));
            Assert.Contains("importScripts('../avalanche_trigger_shim.js')", wrapper);
            Assert.Contains("importScripts('worker.js')", wrapper);
            Assert.False(File.Exists(Path.Combine(dir, ExtensionTrigger.WrapperFile)),
                "the old root seat should be empty");
            Assert.True(ExtensionTrigger.IsArmed(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_Shim_Fires_With_The_Tab_The_Host_Handed_It()
    {
        // Chrome hands the listener the tab the toolbar sat on. The
        // bridge's hidden window is nobody's toolbar: the query a worker
        // makes on its own would name the bridge page or nobody at all.
        // The host knows the open page, so the host names it, and the
        // shim prefers the real tab whose url matches, synthesizing a
        // faithful stand-in only when the profile keeps no such tab.
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{},\"background\":{\"service_worker\":\"sw.js\"}}"),
            ("sw.js", "chrome.action.onClicked.addListener(tab => console.log(tab.url));"));
        try
        {
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            string shim = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.ShimFile));
            Assert.Contains("msg.url", shim);
            Assert.Contains("msg.title", shim);
            Assert.Contains("tabs[i].url === url", shim);
            Assert.Contains("standIn(url, title)", shim);
            Assert.Contains("id: -1", shim);
            string bridge = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.BridgeScriptFile));
            Assert.Contains("location.search", bridge);
            Assert.Contains("avUrl", bridge);
            Assert.Contains("avTitle", bridge);
            Assert.Contains(ExtensionTrigger.MessageTag, bridge);
            using JsonDocument marker = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(dir, ExtensionTrigger.MarkerFile)));
            Assert.Equal(3, marker.RootElement.GetProperty("v").GetInt32());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void V2_Armed_Folder_Upgrades_To_The_Host_Tab_Law()
    {
        // a folder armed by the previous build: the manifest already
        // wears the wrapper, the marker stands at v2, and the shim on
        // disk never heard of the host's named tab. The upgrade recovers
        // the origin worker and rewrites the law in place - it must
        // never wrap a wrapper.
        string dir = WriteDir(
            ("manifest.json", "{\"manifest_version\":3,\"name\":\"T\",\"version\":\"1.0\","
                + "\"action\":{},\"background\":{\"service_worker\":\"avalanche_trigger_sw.js\",\"type\":\"classic\"},"
                + "\"permissions\":[\"tabs\"]}"),
            ("avalanche_trigger_sw.js",
                "importScripts('avalanche_trigger_shim.js');\nimportScripts('sw.js');\n"),
            ("sw.js", "chrome.action.onClicked.addListener(() => {});"),
            ("avalanche_trigger_shim.js", "/* the old shim: it never heard of the host's tab */"),
            ("avalanche_bridge.js", "/* the old bridge: it never named a page */"),
            ("avalanche_bridge.html", "<html></html>"),
            ("avalanche_trigger.json",
                "{\"v\":2,\"armed\":\"2026-10-11T00:00:00Z\",\"kind\":\"worker\","
                + "\"origin\":\"sw.js\",\"wrapper\":\"avalanche_trigger_sw.js\"}"));
        try
        {
            Assert.False(ExtensionTrigger.IsArmed(dir));   // a v2 marker no longer counts
            Assert.True(ExtensionTrigger.EnsureArmed(dir));
            string shim = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.ShimFile));
            Assert.Contains("msg.url", shim);
            Assert.DoesNotContain("never heard of the host's tab", shim);
            string bridge = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.BridgeScriptFile));
            Assert.Contains("avUrl", bridge);
            string wrapper = File.ReadAllText(Path.Combine(dir, ExtensionTrigger.WrapperFile));
            Assert.Contains("importScripts('avalanche_trigger_shim.js')", wrapper);
            Assert.Contains("importScripts('sw.js')", wrapper);
            Assert.DoesNotContain("importScripts('avalanche_trigger_sw.js')", wrapper);
            using JsonDocument marker = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(dir, ExtensionTrigger.MarkerFile)));
            Assert.Equal(3, marker.RootElement.GetProperty("v").GetInt32());
            Assert.Equal("sw.js", marker.RootElement.GetProperty("origin").GetString());
            Assert.True(ExtensionTrigger.IsArmed(dir));
            Assert.False(ExtensionTrigger.EnsureArmed(dir));   // the next pass rests
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
