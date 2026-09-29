using System;
using System.IO;
using System.Text;

namespace Avalanche.Services
{
    /// <summary>
    /// Lightweight always-on file log for the AI citation -> highlight path.
    /// The chain spans async UI callbacks across several classes; when a user
    /// reports "the highlight does nothing" there was previously no way to see
    /// which step died on their machine. One line per step, capped, best-effort:
    /// diagnostics must never break the feature they observe.
    /// </summary>
    internal static class AiHighlightLog
    {
        private static readonly object Gate = new();
        private static string? _path;

        public static void Log(string message)
        {
            try
            {
                lock (Gate)
                {
                    if (_path is null)
                    {
                        var dir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Avalanche", "AI");
                        Directory.CreateDirectory(dir);
                        _path = Path.Combine(dir, "ai-highlight.log");
                    }

                    // Rotate: start fresh once the log outlives its usefulness.
                    if (File.Exists(_path) && new FileInfo(_path).Length > 512 * 1024)
                        File.Delete(_path);

                    File.AppendAllText(
                        _path,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Never throw from diagnostics.
            }
        }
    }
}
