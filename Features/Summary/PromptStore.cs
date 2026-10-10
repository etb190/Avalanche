// Features/Summary/PromptStore.cs - the AI prompts workshop (v1.19.88).
//
// v1.19.90: no prompt text lives in the code any more. Every voice the app
// ships with rides the embedded deck (prompts.default.json, carried inside
// the exe) - on first run the deck is written to
// %LocalAppData%\Avalanche\AI\prompts.json and from then on that file is
// the one source of truth: the reader edits the rows, writes new ones and
// deletes what they don't want from the AI settings' workshop, and Save
// lands the change on disk. A deleted row stays deleted - the deck never
// merges itself back in. The deck's own voice only answers when a feature
// needs a body and the store has none: the factory default, not a second
// copy living in code.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>One prompt row: the id that rides the dropdowns' Tag, the
    /// reader-named title (built-ins carry a TitleKey instead and show the
    /// localized name), the window the prompt serves, and the body the
    /// model is told.</summary>
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

        /// <summary>v1.19.90: whatever path a dropdown takes to render the
        /// row, the reader sees the title - never the type's name.</summary>
        public override string ToString() => DisplayName;
    }

    /// <summary>The store: a flat list persisted as JSON beside the AI
    /// settings, born from the shipped deck on first run. Every mutation
    /// saves and raises Changed so open windows can rebuild their
    /// dropdowns live.</summary>
    public static class PromptStore
    {
        public const string CatWeb = "web";
        public const string CatPdf = "pdf";

        // v1.19.89: the other eight surfaces join the workshop.
        public const string CatSidechat = "sidechat";
        public const string CatWebSidechat = "websidechat";
        public const string CatRecap = "recap";
        public const string CatNotes = "notes";
        public const string CatTester = "tester";
        public const string CatGrammar = "grammar";
        public const string CatRewrite = "rewrite";
        public const string CatEditorSidechat = "editorsidechat";

        private static readonly object _gate = new();
        private static List<AiPromptDef>? _prompts;
        private static List<AiPromptDef>? _deck;   // the shipped voices, parsed once

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
                    if (read != null) return read;   // the reader's file, as it is - deletions included
                }
            }
            catch
            {
                // a corrupt store falls back to the shipped deck; the next save rewrites it whole
            }

            ExtractDefaultStore();
            return DeckCopy();
        }

        // v1.19.90: first run - the deck rides out of the exe and becomes
        // the reader's prompts.json. From here on the deck is never merged
        // back in: a deleted row stays deleted.
        private static void ExtractDefaultStore()
        {
            try
            {
                string json = DefaultDeckJson();
                if (json.Length > 2) File.WriteAllText(StorePath(), json);
            }
            catch
            {
                // a read-only disk still leaves the in-memory deck speaking
            }
        }

        // The shipped voices: prompts.default.json, embedded in the exe
        // (LogicalName Avalanche.Prompts.default.json in the csproj).
        private static string DefaultDeckJson()
        {
            try
            {
                var asm = typeof(PromptStore).Assembly;
                using Stream? stream = asm.GetManifestResourceStream("Avalanche.Prompts.default.json");
                if (stream is null) return "[]";
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch
            {
                return "[]";
            }
        }

        private static List<AiPromptDef> DeckCopy()
        {
            lock (_gate)
            {
                if (_deck is null)
                {
                    try
                    {
                        _deck = System.Text.Json.JsonSerializer.Deserialize<List<AiPromptDef>>(DefaultDeckJson());
                    }
                    catch
                    {
                        // a malformed deck leaves the store without factory voices rather than crashing
                    }

                    _deck ??= new List<AiPromptDef>();
                }

                return new List<AiPromptDef>(_deck);
            }
        }

        /// <summary>The shipped deck's body for an id - the factory voice a
        /// feature speaks when the store's own row is untouched or gone.
        /// Null when the deck never carried the id (the reader's own rows).</summary>
        public static string? DefaultBody(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_gate)
            {
                foreach (AiPromptDef p in DeckCopy())
                {
                    if (string.Equals(p.Id, id, StringComparison.Ordinal))
                    {
                        return string.IsNullOrWhiteSpace(p.Body) ? null : p.Body;
                    }
                }
            }

            return null;
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

        /// <summary>The first body in a category, store order - the
        /// fixed-voice features (the side chats, recap, notes, tester,
        /// grammar, editor side chat) speak it; when every row is empty or
        /// gone the shipped deck's own row answers, and only a category the
        /// deck never knew returns null.</summary>
        public static string? FirstBody(string category)
        {
            EnsureLoaded();
            lock (_gate)
            {
                foreach (AiPromptDef p in _prompts!)
                {
                    if (!string.Equals(p.Category, category, StringComparison.Ordinal)) continue;
                    if (!string.IsNullOrWhiteSpace(p.Body)) return p.Body;
                }

                foreach (AiPromptDef p in DeckCopy())
                {
                    if (!string.Equals(p.Category, category, StringComparison.Ordinal)) continue;
                    if (!string.IsNullOrWhiteSpace(p.Body)) return p.Body;
                }
            }

            return null;
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

        /// <summary>The stored body for an id; an untouched or missing row
        /// speaks the shipped deck's factory voice. PageSummarizer asks here
        /// first.</summary>
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
                        return string.IsNullOrWhiteSpace(p.Body) ? DefaultBody(id) : p.Body;
                    }
                }
            }

            return DefaultBody(id);
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
