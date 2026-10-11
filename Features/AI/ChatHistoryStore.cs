using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// One conversation row in the chat history store. Kind is one of the
    /// Kind* constants; SourceRef is the stable identity of what the
    /// conversation was about - the book's absolute path, the page's URL or
    /// the note's title - so a conversation survives tab churn, restarts and
    /// reopen, while tab-scoped session keys ("wt_...") never could.
    /// </summary>
    public sealed class ChatConversationRecord
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string SourceRef { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public int MessageCount { get; set; }
    }

    /// <summary>
    /// SQLite-backed persistence for sidechat conversations (v1.21.0). One row
    /// per conversation; messages with their citations ride along as JSON so a
    /// restored bubble renders (quote, page, reason) even before - or without -
    /// its document being re-resolved. A NEW conversation is opened implicitly
    /// by New chat; the swept transcript lands here as its own row instead of
    /// dying, so the history picker can always bring it back.
    ///
    /// This store holds READER DATA, not a disposable cache: unlike the vector
    /// index, a schema mismatch never drops the tables - the store disables
    /// itself and logs instead, leaving every past conversation on disk intact.
    /// </summary>
    public sealed class ChatHistoryStore : IDisposable
    {
        public const string KindDocument = "doc";
        public const string KindWeb = "web";
        public const string KindEditor = "editor";

        private const string ConversationsColumns =
            "id,kind,title,source_ref,created_at,updated_at";
        private const string MessagesColumns =
            "id,conversation_id,role,content,timestamp,sources_json";

        private readonly string _dbPath;
        private readonly SqliteConnection _connection;
        private readonly object _dbGate = new object();
        private bool _broken;
        private bool _disposed;

        public ChatHistoryStore(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));

            var directory = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            _connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            _connection.Open();

            try
            {
                Exec(@"CREATE TABLE IF NOT EXISTS conversations (
                        id TEXT PRIMARY KEY,
                        kind TEXT NOT NULL,
                        title TEXT NOT NULL DEFAULT '',
                        source_ref TEXT NOT NULL DEFAULT '',
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL)");
                Exec(@"CREATE TABLE IF NOT EXISTS messages (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        conversation_id TEXT NOT NULL,
                        role INTEGER NOT NULL,
                        content TEXT NOT NULL,
                        timestamp TEXT NOT NULL,
                        sources_json TEXT NOT NULL DEFAULT '')");
                Exec("CREATE INDEX IF NOT EXISTS idx_conv_ctx ON conversations(kind, source_ref, updated_at)");
                Exec("CREATE INDEX IF NOT EXISTS idx_msg_conv ON messages(conversation_id)");

                // Columns must match exactly what the writes below expect. A
                // mismatch never deletes anything - the store just goes dark
                // (reader data outlives schema experiments).
                if (!ColumnsMatch("conversations", ConversationsColumns)
                    || !ColumnsMatch("messages", MessagesColumns))
                {
                    _broken = true;
                    Services.AiHighlightLog.Log(
                        "chat history: schema mismatch on " + _dbPath + " - store disabled, data kept");
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                Services.AiHighlightLog.Log("chat history: init failed: " + ex.Message);
            }
        }

        private void Exec(string sql)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private bool ColumnsMatch(string table, string expectedCsv)
        {
            var expected = expectedCsv.Split(',');
            var actual = new List<string>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({table})";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    actual.Add(reader.GetString(1));
            }
            return expected.Length == actual.Count
                && expected.All(a => actual.Contains(a, StringComparer.OrdinalIgnoreCase));
        }

        // ONE connection, many threads (UI, reply continuations, background
        // records): every public operation enters this reentrant gate, the same
        // law the vector index lives by.
        private T Gate<T>(Func<T> op)
        {
            if (_broken) return default!;
            lock (_dbGate)
            {
                try { return op(); }
                catch (Exception ex)
                {
                    Services.AiHighlightLog.Log("chat history: " + ex.Message);
                    return default!;
                }
            }
        }

        private static string Iso(DateTime utc) => utc.ToString("o");

        /// <summary>Returns the id of the newest conversation for this exact
        /// context (kind + source identity), creating an empty row when none
        /// exists. The caller keeps the id as "its" active conversation.</summary>
        public string EnsureConversation(string kind, string sourceRef, string title)
            => Gate(() =>
            {
                using (var find = _connection.CreateCommand())
                {
                    find.CommandText = @"SELECT id FROM conversations
                        WHERE kind = $kind AND source_ref = $ref
                        ORDER BY updated_at DESC LIMIT 1";
                    find.Parameters.AddWithValue("$kind", kind ?? "");
                    find.Parameters.AddWithValue("$ref", sourceRef ?? "");
                    var found = find.ExecuteScalar();
                    if (found is string id) return id;
                }
                string created = "c" + Guid.NewGuid().ToString("N");
                using (var ins = _connection.CreateCommand())
                {
                    var now = DateTime.UtcNow;
                    ins.CommandText = @"INSERT INTO conversations
                        (id, kind, title, source_ref, created_at, updated_at)
                        VALUES ($id, $kind, $title, $ref, $created, $updated)";
                    ins.Parameters.AddWithValue("$id", created);
                    ins.Parameters.AddWithValue("$kind", kind ?? "");
                    ins.Parameters.AddWithValue("$title", title ?? "");
                    ins.Parameters.AddWithValue("$ref", sourceRef ?? "");
                    ins.Parameters.AddWithValue("$created", Iso(now));
                    ins.Parameters.AddWithValue("$updated", Iso(now));
                    ins.ExecuteNonQuery();
                }
                return created;
            });

        /// <summary>Writes a conversation's full transcript. A null/unknown id
        /// opens a fresh row; a known id is updated in place (created_at kept,
        /// title refreshed, messages replaced wholesale - the snapshot is the
        /// truth, not a delta).</summary>
        public string UpsertConversation(string? id, string kind, string sourceRef,
            string title, IReadOnlyList<ChatMessage> messages)
            => Gate(() =>
            {
                string rowId = (id is { Length: > 0 } given && RowExists(given))
                    ? given
                    : "c" + Guid.NewGuid().ToString("N");
                var now = DateTime.UtcNow;

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        INSERT INTO conversations (id, kind, title, source_ref, created_at, updated_at)
                        VALUES ($id, $kind, $title, $ref, $created, $updated)
                        ON CONFLICT(id) DO UPDATE SET
                            title = $title, updated_at = $updated";
                    cmd.Parameters.AddWithValue("$id", rowId);
                    cmd.Parameters.AddWithValue("$kind", kind ?? "");
                    cmd.Parameters.AddWithValue("$title", title ?? "");
                    cmd.Parameters.AddWithValue("$ref", sourceRef ?? "");
                    cmd.Parameters.AddWithValue("$created", Iso(now));
                    cmd.Parameters.AddWithValue("$updated", Iso(now));
                    cmd.ExecuteNonQuery();
                }

                using (var del = _connection.CreateCommand())
                {
                    del.CommandText = "DELETE FROM messages WHERE conversation_id = $id";
                    del.Parameters.AddWithValue("$id", rowId);
                    del.ExecuteNonQuery();
                }

                foreach (var m in messages ?? Array.Empty<ChatMessage>())
                {
                    if (m is null) continue;
                    using var ins = _connection.CreateCommand();
                    ins.CommandText = @"INSERT INTO messages
                        (conversation_id, role, content, timestamp, sources_json)
                        VALUES ($conv, $role, $content, $ts, $sources)";
                    ins.Parameters.AddWithValue("$conv", rowId);
                    ins.Parameters.AddWithValue("$role", (int)m.MessageRole);
                    ins.Parameters.AddWithValue("$content", m.Content ?? "");
                    ins.Parameters.AddWithValue("$ts", Iso(m.Timestamp));
                    ins.Parameters.AddWithValue("$sources", ChatSourceSnapshots.SerializeSources(m.Sources));
                    ins.ExecuteNonQuery();
                }
                return rowId;
            });

        private bool RowExists(string id)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM conversations WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is not null;
        }

        public ChatConversationRecord? GetConversation(string id)
            => Gate<ChatConversationRecord?>(() =>
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"SELECT id, kind, title, source_ref, created_at, updated_at,
                        (SELECT COUNT(*) FROM messages WHERE conversation_id = conversations.id)
                        FROM conversations WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadRecord(reader) : null;
            });

        public ChatConversationRecord? GetLatestConversation(string kind, string sourceRef)
            => Gate<ChatConversationRecord?>(() =>
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"SELECT id, kind, title, source_ref, created_at, updated_at,
                        (SELECT COUNT(*) FROM messages WHERE conversation_id = conversations.id)
                        FROM conversations
                        WHERE kind = $kind AND source_ref = $ref
                        ORDER BY updated_at DESC LIMIT 1";
                cmd.Parameters.AddWithValue("$kind", kind ?? "");
                cmd.Parameters.AddWithValue("$ref", sourceRef ?? "");
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadRecord(reader) : null;
            });

        /// <summary>Every conversation, newest activity first - the picker's
        /// raw material.</summary>
        public List<ChatConversationRecord> GetSummaries()
            => Gate(() =>
            {
                var list = new List<ChatConversationRecord>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"SELECT id, kind, title, source_ref, created_at, updated_at,
                        (SELECT COUNT(*) FROM messages WHERE conversation_id = conversations.id)
                        FROM conversations ORDER BY updated_at DESC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add(ReadRecord(reader));
                return list;
            });

        private static ChatConversationRecord ReadRecord(SqliteDataReader reader)
        {
            return new ChatConversationRecord
            {
                Id = reader.GetString(0),
                Kind = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Title = reader.IsDBNull(2) ? "" : reader.GetString(2),
                SourceRef = reader.IsDBNull(3) ? "" : reader.GetString(3),
                CreatedAtUtc = ParseIso(reader.IsDBNull(4) ? null : reader.GetString(4)),
                UpdatedAtUtc = ParseIso(reader.IsDBNull(5) ? null : reader.GetString(5)),
                MessageCount = reader.GetInt64(6) is long n ? (int)n : 0,
            };
        }

        private static DateTime ParseIso(string? text)
            => DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? t : DateTime.UtcNow;

        /// <summary>A conversation's messages, oldest first, citations
        /// deserialized - ready to walk back into the bubbles.</summary>
        public List<ChatMessage> GetMessages(string conversationId)
            => Gate(() =>
            {
                var list = new List<ChatMessage>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"SELECT role, content, timestamp, sources_json
                        FROM messages WHERE conversation_id = $id ORDER BY id";
                cmd.Parameters.AddWithValue("$id", conversationId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var role = reader.GetInt32(0);
                    var msg = new ChatMessage
                    {
                        MessageRole = Enum.IsDefined(typeof(ChatMessage.Role), role)
                            ? (ChatMessage.Role)role : ChatMessage.Role.Assistant,
                        Content = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        Timestamp = ParseIso(reader.IsDBNull(2) ? null : reader.GetString(2)),
                        Sources = ChatSourceSnapshots.DeserializeSources(
                            reader.IsDBNull(3) ? "" : reader.GetString(3)),
                    };
                    list.Add(msg);
                }
                return list;
            });

        /// <summary>Deletes a conversation and every message in it. The
        /// reader asked for exactly this - no soft-delete games.</summary>
        public void DeleteConversation(string id)
            => Gate<object?>(() =>
            {
                using (var del = _connection.CreateCommand())
                {
                    del.CommandText = "DELETE FROM messages WHERE conversation_id = $id";
                    del.Parameters.AddWithValue("$id", id);
                    del.ExecuteNonQuery();
                }
                using (var del = _connection.CreateCommand())
                {
                    del.CommandText = "DELETE FROM conversations WHERE id = $id";
                    del.Parameters.AddWithValue("$id", id);
                    del.ExecuteNonQuery();
                }
                return null;
            });

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _connection.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Citations ride the history store as slim JSON: everything a restored
    /// bubble needs to RENDER (id, page, quote, reason) plus everything a
    /// re-resolution needs to make the footnote LIVE again (coordinates, word
    /// range, verification). The resolved chunk itself is deliberately NOT
    /// persisted - it is a live object that belongs to a loaded index.
    /// </summary>
    public static class ChatSourceSnapshots
    {
        private sealed class SourceDto
        {
            public string id { get; set; } = "";
            public int page { get; set; }
            public int pageIndex { get; set; }
            public string quote { get; set; } = "";
            public string reason { get; set; } = "";
            public float[]? coords { get; set; }
            public int[]? words { get; set; }
            public bool verified { get; set; }
            public string location { get; set; } = "Unlocated";
        }

        public static string SerializeSources(IReadOnlyList<AiSource>? sources)
        {
            if (sources is null || sources.Count == 0) return "";
            var dtos = new List<SourceDto>(sources.Count);
            foreach (var s in sources)
            {
                if (s is null) continue;
                dtos.Add(new SourceDto
                {
                    id = s.SourceId ?? "",
                    page = s.PageNumber,
                    pageIndex = s.PageIndex,
                    quote = s.Quote ?? "",
                    reason = s.Reason ?? "",
                    coords = s.PdfCoordinates,
                    words = s.WordRange,
                    verified = s.QuoteVerified,
                    location = s.Location.ToString(),
                });
            }
            return JsonSerializer.Serialize(dtos);
        }

        public static List<AiSource> DeserializeSources(string json)
        {
            var list = new List<AiSource>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                var dtos = JsonSerializer.Deserialize<List<SourceDto>>(json);
                if (dtos is null) return list;
                foreach (var d in dtos)
                {
                    if (d is null) continue;
                    list.Add(new AiSource
                    {
                        SourceId = d.id ?? "",
                        PageNumber = d.page,
                        PageIndex = d.pageIndex,
                        Quote = d.quote ?? "",
                        Reason = d.reason ?? "",
                        PdfCoordinates = d.coords,
                        WordRange = d.words,
                        QuoteVerified = d.verified,
                        Location = Enum.TryParse<AiQuoteLocation>(d.location, out var loc)
                            ? loc : AiQuoteLocation.Unlocated,
                    });
                }
            }
            catch (JsonException)
            {
                // A torn row renders without citations rather than taking the
                // conversation down.
            }
            return list;
        }
    }

    /// <summary>
    /// Restored conversations carry citations whose chunk references were
    /// never persisted (a chunk is a live object of a loaded index). This
    /// re-locates every quote against the freshly loaded index - the same
    /// QuoteLocator the live reply path trusts - so a restored footnote
    /// navigates and highlights again. Unfindable quotes stay unresolved and
    /// the click explains itself instead of moving the reader.
    /// </summary>
    public static class ChatHistoryCitations
    {
        public static int ReResolve(IEnumerable<ChatMessage> messages, IReadOnlyList<DocumentChunk> chunks)
        {
            if (messages is null || chunks is null || chunks.Count == 0) return 0;
            int resolved = 0;
            foreach (var m in messages)
            {
                if (m?.Sources is not { Count: > 0 }) continue;
                bool changed = false;
                foreach (var s in m.Sources)
                {
                    if (s is null || s.ResolvedChunk is not null) continue;
                    string quote = s.Quote;
                    if (string.IsNullOrWhiteSpace(quote)) continue;

                    // Prefer a chunk on the recorded page, then anywhere in the
                    // document - the page number was captured from the very
                    // chunk the citation lived in, so the narrow pass almost
                    // always wins and the wide pass is the repair.
                    DocumentChunk? best = chunks.FirstOrDefault(c =>
                        c.PageIndices.Contains(s.PageIndex)
                        && QuoteLocator.LocateInChunk(c, quote).State != AiQuoteLocation.Unlocated)
                        ?? chunks.FirstOrDefault(c =>
                        QuoteLocator.LocateInChunk(c, quote).State != AiQuoteLocation.Unlocated);
                    if (best is null) continue;

                    var hit = QuoteLocator.LocateInChunk(best, quote);
                    s.ResolvedChunk = best;
                    s.Location = hit.State;
                    s.QuoteVerified = hit.State != AiQuoteLocation.Unlocated;
                    if (s.QuoteVerified)
                    {
                        s.PageIndex = hit.PageIndex;
                        s.PageNumber = hit.PageIndex + 1;
                    }
                    resolved++;
                    changed = true;
                }
                if (changed)
                {
                    // Re-publish the list so the chip row's binding repaints
                    // with the resolved state.
                    var refreshed = m.Sources;
                    m.Sources = new List<AiSource>(refreshed);
                }
            }
            return resolved;
        }
    }
}
