// Services/AxoNotesService.cs - the bridge from Avalanche's generated text
// into Axo's notes database. Axo stores each title's notes as one JSON array
// on disk (<Title>.json beside the Books/Articles database folders), each
// entry a TipTap-friendly HTML fragment with its page span and timestamp.
//
// Strict folder constraint: a note is only ever written when the ACTIVE PDF
// really lives inside the database's Books or Articles tree (any depth).
// A book opened from Downloads, the Desktop root, an external drive or a
// repair-temp copy saves nothing - the caller surfaces that as a gentle
// notice instead of a file.

namespace Avalanche.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;

    /// <summary>One Axo note entry - the exact shape Axo's TipTap reader
    /// expects in its per-title JSON array.</summary>
    internal sealed class AxoNoteItem
    {
        public string Id { get; set; } = "";
        public string Content { get; set; } = "";
        public string StartPage { get; set; } = "";
        public string EndPage { get; set; } = "";
        public string Type { get; set; } = "";
        public string Timestamp { get; set; } = "";
    }

    internal static class AxoNotesService
    {
        // The Axo database roots (Axo's own storage layout). Everything the
        // reader opens OUTSIDE these trees never touches the notes store.
        private const string BooksRoot = @"C:\Users\PC\Desktop\database\Books";
        private const string ArticlesRoot = @"C:\Users\PC\Desktop\database\Articles";

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// True when <paramref name="pdfFilePath"/> sits inside the Books or
        /// Articles database tree; <paramref name="notesJsonPath"/> then receives
        /// the title's note file (<root>\Notes\&lt;Title&gt;.json). Anything else -
        /// Downloads, the Desktop root, external drives, repair-temp copies -
        /// returns false and names no file.
        /// </summary>
        public static bool CanSaveToAxo(string? pdfFilePath, out string? notesJsonPath)
        {
            notesJsonPath = null;
            if (string.IsNullOrWhiteSpace(pdfFilePath))
            {
                return false;
            }

            string kind;
            try
            {
                string full = Path.GetFullPath(pdfFilePath);
                if (IsUnder(full, BooksRoot)) kind = "Books";
                else if (IsUnder(full, ArticlesRoot)) kind = "Articles";
                else return false;

                string title = Path.GetFileNameWithoutExtension(full);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return false;
                }

                // Compose from the SAME root the file matched, so the folder
                // rule and the note destination can never desync.
                string root = kind == "Books" ? BooksRoot : ArticlesRoot;
                notesJsonPath = Path.Combine(root, "Notes", title + ".json");
                return true;
            }
            catch
            {
                // a malformed path is simply not a database book
                notesJsonPath = null;
                return false;
            }
        }

        // Prefix check that respects folder boundaries: "C:\...\Books\X" is
        // inside Books, "C:\...\Books Miscellaneous\X" is NOT.
        private static bool IsUnder(string fullPath, string root)
        {
            // Separator-normalized: the roots are literal Windows folders, so
            // both sides fold to backslash before the (case-insensitive)
            // prefix test, whatever separator the inputs carry.
            string normFull = fullPath.Replace('/', '\\');
            string normRoot = root.Replace('/', '\\').TrimEnd('\\') + '\\';
            return normFull.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Appends one note to the title's Axo JSON (creating the file and the
        /// Notes folder when they do not exist). Returns false - writing
        /// nothing - when the PDF sits outside the database folders or the
        /// write fails; the caller informs the reader, the store stays clean.
        /// The write is atomic: the new array lands via a temp file replace,
        /// so a crash mid-write can never leave a torn JSON behind.
        /// </summary>
        public static Task<bool> AppendNoteAsync(string? pdfFilePath, string markdownContent,
            int startPage, int endPage)
        {
            return Task.Run(() =>
            {
                if (!CanSaveToAxo(pdfFilePath, out string? jsonPath) || jsonPath is null)
                {
                    return false;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);

                    var notes = new List<AxoNoteItem>();
                    if (File.Exists(jsonPath))
                    {
                        string existing = File.ReadAllText(jsonPath);
                        if (!string.IsNullOrWhiteSpace(existing))
                        {
                            var loaded = JsonSerializer.Deserialize<List<AxoNoteItem>>(existing, ReadOptions);
                            if (loaded is not null) notes = loaded;
                        }
                    }

                    notes.Add(new AxoNoteItem
                    {
                        Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            .ToString(CultureInfo.InvariantCulture),
                        Content = MarkdownToHtml(markdownContent),
                        StartPage = Math.Max(1, startPage).ToString(CultureInfo.InvariantCulture),
                        EndPage = Math.Max(1, endPage).ToString(CultureInfo.InvariantCulture),
                        Type = "general",
                        Timestamp = DateTime.UtcNow.ToString("o")
                    });

                    string json = JsonSerializer.Serialize(notes, WriteOptions);
                    string tmp = jsonPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(jsonPath))
                    {
                        File.Replace(tmp, jsonPath, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(tmp, jsonPath);
                    }

                    return true;
                }
                catch
                {
                    // a held file, a full disk, a locked folder - the note is
                    // not written and the caller says so; never take the app down
                    return false;
                }
            });
        }

        /// <summary>
        /// Markdown → the clean HTML Axo's TipTap editor renders: paragraphs,
        /// bold, italic and bullet lists. HTML-hostile characters are escaped
        /// first, then the markdown markers are folded into tags.
        /// </summary>
        internal static string MarkdownToHtml(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return string.Empty;
            }

            string[] lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var html = new StringBuilder();
            var paragraph = new List<string>();
            var bullets = new List<string>();

            void FlushParagraph()
            {
                if (paragraph.Count == 0) return;
                html.Append("<p>")
                    .Append(string.Join("<br />", paragraph.Select(HtmlInline)))
                    .Append("</p>");
                paragraph.Clear();
            }

            void FlushBullets()
            {
                if (bullets.Count == 0) return;
                html.Append("<ul>");
                foreach (string item in bullets)
                {
                    html.Append("<li>").Append(HtmlInline(item)).Append("</li>");
                }
                html.Append("</ul>");
                bullets.Clear();
            }

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    FlushParagraph();
                    FlushBullets();
                    continue;
                }

                if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("\u2022 "))
                {
                    FlushParagraph();
                    bullets.Add(line[2..].Trim());
                    continue;
                }

                if (line.StartsWith("#"))
                {
                    FlushParagraph();
                    FlushBullets();
                    // A markdown heading reads as a bold paragraph in Axo.
                    html.Append("<p><strong>").Append(HtmlInline(line.TrimStart('#').Trim()))
                        .Append("</strong></p>");
                    continue;
                }

                FlushBullets();
                paragraph.Add(line);
            }

            FlushParagraph();
            FlushBullets();
            return html.ToString();
        }

        // Escape HTML, then fold the inline markers. Bold runs first so the
        // italic pass never lands inside an emitted tag.
        private static string HtmlInline(string text)
        {
            string escaped = text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");

            escaped = System.Text.RegularExpressions.Regex.Replace(
                escaped, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            escaped = System.Text.RegularExpressions.Regex.Replace(
                escaped, "(?<!\\*)\\*([^*\\n]+)\\*(?!\\*)", "<em>$1</em>");
            escaped = System.Text.RegularExpressions.Regex.Replace(
                escaped, "__(.+?)__", "<strong>$1</strong>");
            return escaped;
        }
    }
}
