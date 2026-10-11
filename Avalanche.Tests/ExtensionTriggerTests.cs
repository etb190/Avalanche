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
}
