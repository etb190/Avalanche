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
//
// v1.19.93: one careful exception to "never merges back in" - a body that is
// byte-identical to a PREVIOUS factory voice is a factory echo, not a reader
// edit, and graduates to the current deck body so a prompt overhaul reaches
// the installs that never opened the workshop. A body that differs by a
// single letter is the reader's own wording and is never touched.

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

        // The pre-overhaul factory voices (v1.19.90-92), fingerprinted: a
        // stored body whose SHA-256 matches one of these was never edited by
        // the reader - the first run's deck extraction wrote it and nothing
        // else ever did. Only those graduate to the current deck body.
        private static readonly Dictionary<string, string> LegacyBodyHashes = new()
        {
            ["nonfiction_classic"] = "71d0db20a95e19b4d828ceaf69a5c974996d8aa406e2f2949c9906fa633ef53b",
            ["fiction"] = "004e3cfe27f54510b6fd0afb1c9f2402f524c2e772807e043ebefd35e1af7d0c",
            ["philosophical_fiction"] = "af0f044cb3089906c62010920d72fa03662c5b1889defe9bb77ceaf5d14bc11c",
            ["research_papers"] = "7deaccd97248f17ebea2e1c7d8234ae65cf6ad429bb0ab1d8e1047af2ab1531d",
            ["self_help"] = "e41d5bf86b2d50f980a030509672c173487928168ce1cf79c36e7659eb81a96b",
            ["law"] = "9c7d7d8122c46e69563f9fc62d0b137c95510b93bf37b8bff966b8fb94cb4b8d",
            ["web_nonfiction_classic"] = "71d0db20a95e19b4d828ceaf69a5c974996d8aa406e2f2949c9906fa633ef53b",
        };

        private static bool IsLegacyFactoryBody(string id, string body)
        {
            if (!LegacyBodyHashes.TryGetValue(id, out string? legacy)) return false;
            using var sha = System.Security.Cryptography.SHA256.Create();
            string hex = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(body)));
            return string.Equals(hex, legacy, StringComparison.OrdinalIgnoreCase);
        }

        // Graduates factory echoes to the current deck bodies. Returns true
        // when something changed and the store should be re-written.
        private static bool GraduateLegacyBodies(List<AiPromptDef> rows)
        {
            bool changed = false;
            foreach (AiPromptDef row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Body)) continue;
                if (!IsLegacyFactoryBody(row.Id, row.Body)) continue;
                string? fresh = DefaultBody(row.Id);
                if (fresh is not null && !string.Equals(fresh, row.Body, StringComparison.Ordinal))
                {
                    row.Body = fresh;
                    changed = true;
                }
            }

            return changed;
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
                    if (read != null)
                    {
                        // Factory echoes step up to the current deck voice; the
                        // reader's own wording never moves. Deletions included.
                        if (GraduateLegacyBodies(read)) WriteStore(read);
                        return read;
                    }
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
                WriteStore(_prompts!);
                Changed?.Invoke();
            }
            catch
            {
                // best-effort persistence; a read-only disk must not crash the workshop
                // and a listener must never break the saver
            }
        }

        // The byte-level writer both the workshop and the graduation share.
        private static void WriteStore(List<AiPromptDef> rows)
        {
            string json = System.Text.Json.JsonSerializer.Serialize(rows,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StorePath(), json);
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
