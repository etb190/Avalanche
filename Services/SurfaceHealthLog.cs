// Services/SurfaceHealthLog.cs — black-box logger for the surface self-healing
// system. Append-only, thread-safe, self-rolling at 512 KB. Swallows every I/O
// fault so diagnostics can never crash the app.
//
// Default sink: %LOCALAPPDATA%\SurfaceHealth\surface-health.log
// Optionally mirror into the app's own logger:
//   SurfaceHealthLog.SetSink(line => existingLogger.Debug(line));

namespace Avalanche.Services
{
    using System;
    using System.IO;

    internal static class SurfaceHealthLog
    {
        private static readonly object Gate = new object();
        private static string? _path;
        private static Action<string>? _sink;
        private const long RollSizeBytes = 512 * 1024;

        /// <summary>Mirror every line into the app's existing logging pipeline.</summary>
        public static void SetSink(Action<string> sink)
        {
            _sink = sink;
        }

        public static void Log(string message)
        {
            string line = DateTime.UtcNow.ToString("o") + " [surface] " + message;
            Action<string>? sink = _sink;
            if (sink != null)
            {
                try
                {
                    sink(line);
                }
                catch
                {
                    // never let diagnostics break the app
                }
            }

            try
            {
                lock (Gate)
                {
                    string path = GetPath();
                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length > RollSizeBytes)
                    {
                        string old = path + ".old";
                        if (File.Exists(old))
                        {
                            File.Delete(old);
                        }

                        File.Move(path, old);
                    }

                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // best-effort only
            }
        }

        private static string GetPath()
        {
            if (_path != null)
            {
                return _path;
            }

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SurfaceHealth");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "surface-health.log");
            return _path;
        }
    }
}
