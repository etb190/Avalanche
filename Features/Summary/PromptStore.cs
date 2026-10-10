// Features/Summary/PromptStore.cs - the AI prompts workshop (v1.19.88).
//
// The summary prompts stopped being hardcoded wiring: every voice the
// digest windows speak is a row here - a title, a category (the browser
// page's digest or the book window's) and a body. The reader edits the
// built-in voices, writes new ones, deletes what they don't want; Save
// lands the row in %LocalAppData%\Avalanche\AI\prompts.json and every
// open window hears the Changed event. A row with an empty body lets the
// hardcoded voice keep speaking (PageSummarizer's own mandate); the
// moment a body is saved it takes the seat.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>One prompt row: the id that rides the dropdowns' Tag, the
    /// reader-named title (built-ins carry a TitleKey instead and show the
    /// localized name), the window the prompt serves, and the body the
    /// model is told - empty meaning the built-in voice still speaks.</summary>
    public sealed class AiPromptDef
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string TitleKey { get; set; } = "";
        public string Category { get; set; } = PromptStore.CatPdf;
        public string Body { get; set; } = "";

        /// <summary>The name the dropdowns show: the reader's title first,
        /// then the built-in's localized name, then the id.</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                if (!string.IsNullOrWhiteSpace(TitleKey))
                {
                    try
                    {
                        return System.Windows.Application.Current?.TryFindResource(TitleKey) as string ?? Id;
                    }
                    catch
                    {
                        return Id;
                    }
                }

                return Id;
            }
        }
    }

    /// <summary>The store: a flat list persisted as JSON beside the AI
    /// settings, seeded on first run with the browser's nonfiction classic
    /// and the book window's six personas. Every mutation saves and raises
    /// Changed so open windows can rebuild their dropdowns live.</summary>
    public static class PromptStore
    {
        public const string CatWeb = "web";
        public const string CatPdf = "pdf";

        private static readonly object _gate = new();
        private static List<AiPromptDef>? _prompts;

        /// <summary>Raised after every save or delete so open windows can
        /// rebuild their prompt dropdowns while the workshop edits.</summary>
        public static event Action? Changed;

        private static string StorePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avalanche", "AI");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "prompts.json");
        }

        private static void EnsureLoaded()
        {
            if (_prompts != null) return;
            lock (_gate)
            {
                if (_prompts != null) return;
                _prompts = Load();
            }
        }

        private static List<AiPromptDef> Load()
        {
            try
            {
                string path = StorePath();
                if (File.Exists(path))
                {
                    var read = System.Text.Json.JsonSerializer.Deserialize<List<AiPromptDef>>(
                        File.ReadAllText(path));
                    if (read != null) return read;
                }
            }
            catch
            {
                // a corrupt store falls back to the seed; the next save rewrites it whole
            }

            return Seed();
        }

        private static void Save()
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(_prompts,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StorePath(), json);
            }
            catch
            {
                // best-effort persistence; a read-only disk must not crash the workshop
            }

            try { Changed?.Invoke(); }
            catch { /* a listener must never break the saver */ }
        }

        // The born-with rows: the web window's single voice and the book
        // navigator's six personas - the ids are the canonical genre tags
        // PageSummarizer has always understood, so the restore paths and
        // the hardcoded mandates keep working untouched.
        private static List<AiPromptDef> Seed() => new()
        {
            new AiPromptDef { Id = "web_nonfiction_classic", TitleKey = "Str_Genre_Nonfiction", Category = CatWeb },
            new AiPromptDef { Id = "nonfiction_classic", TitleKey = "Str_Genre_Nonfiction", Category = CatPdf },
            new AiPromptDef { Id = "fiction", TitleKey = "Str_Genre_Fiction", Category = CatPdf },
            new AiPromptDef { Id = "philosophical_fiction", TitleKey = "Str_Genre_Philosophical", Category = CatPdf },
            new AiPromptDef { Id = "research_papers", TitleKey = "Str_Genre_Research", Category = CatPdf },
            new AiPromptDef { Id = "self_help", TitleKey = "Str_Genre_SelfHelp", Category = CatPdf },
            new AiPromptDef { Id = "law", TitleKey = "Str_Genre_Law", Category = CatPdf }
        };

        /// <summary>Every row, a defensive copy - the caller may do what it
        /// likes with the list it gets.</summary>
        public static List<AiPromptDef> All()
        {
            EnsureLoaded();
            lock (_gate) { return new List<AiPromptDef>(_prompts!); }
        }

        /// <summary>The rows serving one window, in store order.</summary>
        public static List<AiPromptDef> For(string category)
        {
            EnsureLoaded();
            lock (_gate)
            {
                var list = new List<AiPromptDef>();
                foreach (AiPromptDef p in _prompts!)
                {
                    if (string.Equals(p.Category, category, StringComparison.Ordinal)) list.Add(p);
                }

                return list;
            }
        }

        public static bool Exists(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            EnsureLoaded();
            lock (_gate)
            {
                foreach (AiPromptDef p in _prompts!)
                {
                    if (string.Equals(p.Id, id, StringComparison.Ordinal)) return true;
                }
            }

            return false;
        }

        /// <summary>The stored body for an id, or null when the hardcoded
        /// voice should keep speaking. PageSummarizer asks here first.</summary>
        public static string? StoredMandate(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureLoaded();
            lock (_gate)
            {
                foreach (AiPromptDef p in _prompts!)
                {
                    if (string.Equals(p.Id, id, StringComparison.Ordinal))
                    {
                        return string.IsNullOrWhiteSpace(p.Body) ? null : p.Body;
                    }
                }
            }

            return null;
        }

        /// <summary>Insert or update by id, then save - and the Changed
        /// event fires. We click save and that prompt is saved.</summary>
        public static void Upsert(AiPromptDef def)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.Id)) return;
            EnsureLoaded();
            lock (_gate)
            {
                AiPromptDef? hit = null;
                foreach (AiPromptDef p in _prompts!)
                {
                    if (string.Equals(p.Id, def.Id, StringComparison.Ordinal)) { hit = p; break; }
                }

                if (hit == null) _prompts!.Add(def);
                else
                {
                    hit.Title = def.Title;
                    hit.TitleKey = def.TitleKey;
                    hit.Category = def.Category;
                    hit.Body = def.Body;
                }

                Save();
            }
        }

        public static void Delete(string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            EnsureLoaded();
            lock (_gate)
            {
                int removed = _prompts!.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal));
                if (removed > 0) Save();
            }
        }

        /// <summary>An id for a brand-new row - unique enough, sortable,
        /// never colliding with the built-in genre tags.</summary>
        public static string NewId() => "p_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
