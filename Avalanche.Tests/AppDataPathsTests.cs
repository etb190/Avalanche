using System.IO;
using System.Text.Json;
using Avalanche.Services;
using Xunit;

namespace Avalanche.Tests;

[Collection("Environment variables")]
public sealed class AppDataPathsTests
{
    [Fact]
    public void PortableSettingsAreStoredBesideTheLauncher()
    {
        string root = Path.Combine(Path.GetTempPath(), $"avalanche-portable-data-{Guid.NewGuid():N}");
        string launcher = Path.Combine(root, "Avalanche-Portable.exe");
        string? previous = Environment.GetEnvironmentVariable("AVALANCHE_LAUNCHER_PATH");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(launcher, []);
            Environment.SetEnvironmentVariable("AVALANCHE_LAUNCHER_PATH", launcher);

            AppDataPaths.SetPortableSetting("Locale", "ja-JP");

            string dataRoot = Path.Combine(root, "Avalanche-Data");
            Assert.Equal(dataRoot, AppDataPaths.PortableRoot);
            Assert.Equal("ja-JP", AppDataPaths.GetPortableSetting("Locale"));
            Dictionary<string, string>? settings = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(dataRoot, "settings.json")));
            Assert.Equal("ja-JP", settings?["Locale"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AVALANCHE_LAUNCHER_PATH", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;
