using System.IO;
using System.Text.Json;

namespace Avalanche.Services;

internal static class AppDataPaths
{
    private const string LauncherPathVariable = "AVALANCHE_LAUNCHER_PATH";
    private const string MigrationMarker = "data.migrated.129";
    private static readonly Lock SettingsGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avalanche");

    // v1.29.0: the launcher-relative root the OLD law used for portable runs.
    // It is the migration SOURCE now, never the data home: a new download in a
    // new folder arrived with an empty Avalanche-Data and the reader's
    // bookmarks, tabs, browser profile, extensions and editor sessions died
    // with the old folder. EnsureMigrated carries whatever this root still
    // holds into the profile, once.
    internal static string? LegacyPortableRoot
    {
        get
        {
            try
            {
                string? launcher = Environment.GetEnvironmentVariable(LauncherPathVariable);
                if (string.IsNullOrWhiteSpace(launcher)) return null;
                string fullPath = Path.GetFullPath(launcher);
                if (!File.Exists(fullPath)) return null;
                string? directory = Path.GetDirectoryName(fullPath);
                return directory is null ? null : Path.Combine(directory, "Avalanche-Data");
            }
            catch { return null; }
        }
    }

    // v1.29.0: the data home is ALWAYS the user's Windows profile - the
    // portable exe, the per-user install and the machine-wide install all
    // read and write this one root, so a new version of the app never
    // arrives empty-handed again.
    internal static string UserRoot => LocalRoot;
    internal static string SettingsFile => Path.Combine(UserRoot, "settings.json");
    internal static string SignaturesFile => Path.Combine(UserRoot, "signatures.json");
    internal static string TessDataDirectory => Path.Combine(UserRoot, "tessdata");

    // General key-value settings, always the profile root's settings.json
    // (v1.29.0: the installed builds' registry branch retired - the migration
    // folds those values into the same file, and one file is the one truth).
    internal static string? GetSetting(string name)
    {
        lock (SettingsGate)
        {
            return ReadSettings().GetValueOrDefault(name);
        }
    }

    internal static void SetSetting(string name, string value)
    {
        lock (SettingsGate)
        {
            Dictionary<string, string> settings = ReadSettings();
            settings[name] = value;
            WriteSettings(settings);
        }
    }

    internal static void RemoveSetting(string name)
    {
        lock (SettingsGate)
        {
            Dictionary<string, string> settings = ReadSettings();
            if (!settings.Remove(name)) return;
            WriteSettings(settings);
        }
    }

    // ── v1.29.0: the one-time migration ────────────────────────────────────
    // Runs first thing at startup, before any setting is read. Copies the
    // legacy launcher-side root into the profile - settings merged key by
    // key (the profile wins on clashes), every other file and folder copied
    // only when the profile does not have its own - and folds the installed
    // builds' registry values into the same file. A marker in the target
    // settings.json makes it a single visit; a deleted prompt or a newer
    // bookmark can never be dragged back by a re-run, and a folder the
    // reader kept for another day is never deleted.
    internal static void EnsureMigrated(string? targetRootOverride = null, string? legacyRootOverride = null)
    {
        try
        {
            string target = targetRootOverride ?? UserRoot;
            string targetSettings = Path.Combine(target, "settings.json");
            lock (SettingsGate)
            {
                Dictionary<string, string> settings = ReadSettingsFrom(targetSettings);
                if (settings.ContainsKey(MigrationMarker)) return;

                string? legacy = legacyRootOverride ?? LegacyPortableRoot;
                if (legacy is not null && Directory.Exists(legacy))
                {
                    bool sameRoot;
                    try
                    {
                        sameRoot = string.Equals(
                            Path.GetFullPath(legacy), Path.GetFullPath(target),
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch { sameRoot = false; }

                    if (!sameRoot)
                    {
                        // settings first: the legacy keys fill the gaps
                        string legacySettings = Path.Combine(legacy, "settings.json");
                        foreach ((string key, string value) in ReadSettingsFrom(legacySettings))
                        {
                            if (!settings.ContainsKey(key)) settings[key] = value;
                        }

                        // then everything else the old root still holds
                        foreach (string entry in Directory.EnumerateFileSystemEntries(legacy))
                        {
                            try
                            {
                                string name = Path.GetFileName(entry);
                                if (string.Equals(name, "settings.json", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                string destination = Path.Combine(target, name);
                                if (File.Exists(entry))
                                {
                                    if (!File.Exists(destination)) File.Copy(entry, destination);
                                }
                                else if (Directory.Exists(entry))
                                {
                                    if (!Directory.Exists(destination)) CopyDirectory(entry, destination);
                                }
                            }
                            catch
                            {
                                // one locked file must not stop the rest of the move
                            }
                        }
                    }
                }

                // The installed builds' registry settings move in too (the
                // real profile only - the test's override stands alone).
                if (targetRootOverride is null)
                {
                    try
                    {
                        using Microsoft.Win32.RegistryKey? key =
                            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Avalanche\Settings");
                        if (key is not null)
                        {
                            foreach (string name in key.GetValueNames())
                            {
                                if (settings.ContainsKey(name)) continue;
                                if (key.GetValue(name) is string value) settings[name] = value;
                            }
                        }
                    }
                    catch
                    {
                        // not Windows, or the hive refused - the file stands
                    }
                }

                settings[MigrationMarker] = "1";
                WriteSettingsTo(targetSettings, settings);
            }
        }
        catch
        {
            // a migration that could not run is retried on the next start;
            // it must never stand between the app and its window
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (string dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static Dictionary<string, string> ReadSettings() => ReadSettingsFrom(SettingsFile);

    private static void WriteSettings(Dictionary<string, string> settings) => WriteSettingsTo(SettingsFile, settings);

    private static Dictionary<string, string> ReadSettingsFrom(string file)
    {
        try
        {
            if (!File.Exists(file)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(file))
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch { return new Dictionary<string, string>(StringComparer.Ordinal); }
    }

    private static void WriteSettingsTo(string file, Dictionary<string, string> settings)
    {
        // v1.19.63: a settings write is best effort, never a crash. The old
        // single Move(overwrite) died loudly when an antivirus held the fresh
        // file, a sync client locked the folder, or the settings file carried
        // a read-only bit - and one such death, mid-window-construction, took
        // the whole app down with an UnauthorizedAccessException. Now: retries
        // with backoff, the read-only bit cleared, the temp file discarded
        // between attempts, and after every fallback a silent give-up - a
        // lost preference is recovered by moving on, a lost app is not.
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                string temporary = file + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");
                    File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
                    try
                    {
                        if (File.Exists(file)
                            && File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly))
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                        }
                    }
                    catch
                    {
                        // the move below reports what actually matters
                    }

                    File.Move(temporary, file, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                {
                    try { File.Delete(temporary); } catch { /* rebuilt next attempt */ }
                    Thread.Sleep(40 * (attempt + 1));
                }
            }
        }
        catch
        {
            // every retry lost: the write is dropped rather than taking the
            // caller - often a UI event handler mid-window-construction - down.
        }
    }
}
