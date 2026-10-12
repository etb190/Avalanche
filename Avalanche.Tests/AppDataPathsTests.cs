using System.IO;
using System.Text.Json;
using Avalanche.Services;
using Xunit;

namespace Avalanche.Tests;

[Collection("Environment variables")]
public sealed class AppDataPathsTests
{
    [Fact]
    public void TheDataRootIsAlwaysTheProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), $"avalanche-portable-data-{Guid.NewGuid():N}");
        string launcher = Path.Combine(root, "Avalanche-Portable.exe");
        string? previous = Environment.GetEnvironmentVariable("AVALANCHE_LAUNCHER_PATH");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(launcher, []);
            Environment.SetEnvironmentVariable("AVALANCHE_LAUNCHER_PATH", launcher);

            // v1.29.0: the data home never moves with the launcher again -
            // a new download in a new folder arrives with its data intact.
            Assert.Equal(AppDataPaths.LocalRoot, AppDataPaths.UserRoot);
            Assert.True(AppDataPaths.SettingsFile.StartsWith(AppDataPaths.LocalRoot, StringComparison.Ordinal));
            Assert.EndsWith("settings.json", AppDataPaths.SettingsFile);
            Assert.Equal(Path.Combine(root, "Avalanche-Data"), AppDataPaths.LegacyPortableRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AVALANCHE_LAUNCHER_PATH", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheLegacyFolderMigratesIntoTheProfileOnce()
    {
        string legacy = Path.Combine(Path.GetTempPath(), $"avalanche-legacy-{Guid.NewGuid():N}");
        string target = Path.Combine(Path.GetTempPath(), $"avalanche-profile-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(legacy, "WebView2Data"));
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(legacy, "bookmarks.json"), "[{\"Name\":\"kept\"}]");
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"Locale\":\"ja-JP\"}");
            File.WriteAllText(Path.Combine(legacy, "WebView2Data", "cookies"), "x");
            // The profile already lives: its settings win, and a bookmark of
            // its own is never dragged back to an older copy.
            File.WriteAllText(Path.Combine(target, "settings.json"), "{\"Theme\":\"dark\"}");
            File.WriteAllText(Path.Combine(target, "bookmarks.json"), "[{\"Name\":\"newer\"}]");

            AppDataPaths.EnsureMigrated(targetRootOverride: target, legacyRootOverride: legacy);

            // The profile's own bookmark survives - a newer copy is never
            // dragged back to the legacy folder's older one.
            Assert.Equal("newer", JsonName(File.ReadAllText(Path.Combine(target, "bookmarks.json"))));
            Assert.True(File.Exists(Path.Combine(target, "WebView2Data", "cookies")));
            Dictionary<string, string>? settings = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(target, "settings.json")));
            Assert.Equal("dark", settings?["Theme"]);
            Assert.Equal("ja-JP", settings?["Locale"]);
            Assert.Equal("1", settings?["data.migrated.129"]);

            // One visit: a later change in the legacy folder stays there.
            File.WriteAllText(Path.Combine(legacy, "bookmarks.json"), "[{\"Name\":\"later\"}]");
            AppDataPaths.EnsureMigrated(targetRootOverride: target, legacyRootOverride: legacy);
            Assert.Equal("newer", JsonName(File.ReadAllText(Path.Combine(target, "bookmarks.json"))));
        }
        finally
        {
            if (Directory.Exists(legacy)) Directory.Delete(legacy, recursive: true);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    private static string JsonName(string json)
    {
        var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement[0].GetProperty("Name").GetString() ?? "";
    }
}

[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;
