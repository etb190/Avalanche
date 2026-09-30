// Services/DiagnosticsBundle.cs - one-file evidence dump for the black-screen /
// crash chase. surface-health.log uploads keep failing in the user's chat
// channel while pasted text arrives fine, so the app produces and transports
// the evidence itself:
//   * Desktop\avalanche-diagnostics-<timestamp>.txt (timestamped, newest 5 kept,
//     so a false-positive can never overwrite real evidence)
//   * clipboard copy - caller on the UI thread only (SetDataObject copy:true
//     keeps the text alive after the process is killed)
// Bundle = env header + surface-health.log tail (+ .old) + latest crash log.
// Safe on any thread (the hang watchdog calls it while the dispatcher is dead);
// every fault is swallowed because diagnostics must never crash the app.

namespace Avalanche.Services
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Windows.Media;

    internal static class DiagnosticsBundle
    {
        private const int LogTailLines = 300;
        private const int OldLogTailLines = 100;
        private const int CrashTailLines = 80;
        private const int KeepDumps = 5;

        /// <summary>Builds the bundle and writes a timestamped file to the Desktop.
        /// Returns the text (clipboard copying is the caller's job and must run on
        /// the UI thread).</summary>
        public static string Dump(string reason)
        {
            string text = Build(reason);
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "avalanche-diagnostics-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path, text);
                PruneOldDumps();
                SurfaceHealthLog.Log(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "diagnostics bundle written ({0}): {1} chars -> {2}",
                    reason,
                    text.Length,
                    path));
            }
            catch (Exception ex)
            {
                SurfaceHealthLog.Log("diagnostics bundle write FAILED: " + ex.Message);
            }

            return text;
        }

        public static string Build(string reason)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Avalanche diagnostics bundle ===");
            sb.AppendLine("reason : " + reason);
            sb.AppendLine("time   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            sb.AppendLine("version: v" + AppVersion.Display);
            sb.AppendLine("os     : " + Environment.OSVersion.VersionString +
                          (Environment.Is64BitProcess ? " (64-bit)" : " (32-bit)"));
            try
            {
                sb.AppendLine(
                    "render : " + (Avalanche.App.SoftwareRenderingForced ? "SOFTWARE (forced)" : "HARDWARE") +
                    ", tier " + (RenderCapability.Tier >> 16));
            }
            catch
            {
                sb.AppendLine("render : (unavailable)");
            }

            sb.AppendLine();

            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SurfaceHealth", "surface-health.log");
            sb.AppendLine("=== surface-health.log tail ===");
            sb.AppendLine(TailOfFile(logPath, LogTailLines));
            string oldLog = logPath + ".old";
            if (File.Exists(oldLog))
            {
                sb.AppendLine("=== surface-health.log.old tail ===");
                sb.AppendLine(TailOfFile(oldLog, OldLogTailLines));
            }

            string? crash = LatestCrashLog();
            if (crash != null)
            {
                sb.AppendLine("=== latest crash log: " + Path.GetFileName(crash) + " ===");
                sb.AppendLine(TailOfFile(crash, CrashTailLines));
            }

            return sb.ToString();
        }

        private static string? LatestCrashLog()
        {
            try
            {
                var dir = new DirectoryInfo(Avalanche.CrashReporter.LogDir);
                if (!dir.Exists)
                {
                    return null;
                }

                return dir.GetFiles("crash_*.log")
                          .OrderByDescending(f => f.LastWriteTime)
                          .Select(f => f.FullName)
                          .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Last N lines of a file, best-effort; shares with writers.</summary>
        private static string TailOfFile(string path, int lines)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return "(missing)";
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var ring = new Queue<string>(lines);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (ring.Count == lines)
                    {
                        ring.Dequeue();
                    }

                    ring.Enqueue(line);
                }

                return ring.Count == 0 ? "(empty)" : string.Join(Environment.NewLine, ring);
            }
            catch (Exception ex)
            {
                return "(read failed: " + ex.Message + ")";
            }
        }

        private static void PruneOldDumps()
        {
            try
            {
                var desktop = new DirectoryInfo(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                var dumps = desktop.GetFiles("avalanche-diagnostics-*.txt")
                                   .OrderByDescending(f => f.LastWriteTime)
                                   .ToList();
                for (int i = KeepDumps; i < dumps.Count; i++)
                {
                    try
                    {
                        dumps[i].Delete();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
    }
}
