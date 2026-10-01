// Features/Summary/SummaryCache.cs — persistent cache for generated page summaries.
//
// Stores finished digests in the AI database (vector_index.db) in its own table so
// re-opening the same range is instant and free. Keyed by document id + page range
// + model + prompt version, and validated by a SHA-256 of the extracted page text:
// if the file's text ever changes, the hash mismatch invalidates the stale summary.
// VectorIndex's drift logic only drops its own tables, so this table is safe here.

namespace Avalanche.Features.Summary
{
    using System;
    using System.IO;
    using System.Text;
    using System.Security.Cryptography;
    using Microsoft.Data.Sqlite;

    internal static class SummaryCache
    {
        // v4: digests are buffered and hardened by the prose guard (v1.8.86). v3 entries
        // can hold bullet-list digests from stubborn models; v2 plain-prose and v1 markdown
        // digests must never be served from cache either.
        private const int PromptVersion = 4;
        private static readonly object Gate = new();
        private static SqliteConnection? _connection;

        public static string HashText(string text)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes);
        }

        public static string? Get(string documentId, int firstPage, int lastPage, string model, string contentHash)
        {
            try
            {
                lock (Gate)
                {
                    using var cmd = Connection().CreateCommand();
                    cmd.CommandText =
                        "SELECT content FROM summary_cache WHERE document_id=$d AND " +
                        "first_page=$f AND last_page=$l AND model=$m AND prompt_version=$v AND content_hash=$h";
                    cmd.Parameters.AddWithValue("$d", documentId);
                    cmd.Parameters.AddWithValue("$f", firstPage);
                    cmd.Parameters.AddWithValue("$l", lastPage);
                    cmd.Parameters.AddWithValue("$m", model);
                    cmd.Parameters.AddWithValue("$v", PromptVersion);
                    cmd.Parameters.AddWithValue("$h", contentHash);
                    using var reader = cmd.ExecuteReader();
                    if (!reader.Read())
                    {
                        return null;
                    }

                    return reader.IsDBNull(0) ? null : reader.GetString(0);
                }
            }
            catch
            {
                return null; // cache must never break generation
            }
        }

        public static void Put(
            string documentId, int firstPage, int lastPage, string model,
            string contentHash, string content, int wordCount)
        {
            try
            {
                lock (Gate)
                {
                    using var cmd = Connection().CreateCommand();
                    cmd.CommandText =
                        "INSERT OR REPLACE INTO summary_cache(document_id, first_page, last_page, model, " +
                        "prompt_version, content_hash, content, word_count, created_utc) " +
                        "VALUES($d,$f,$l,$m,$v,$h,$c,$w,$t)";
                    cmd.Parameters.AddWithValue("$d", documentId);
                    cmd.Parameters.AddWithValue("$f", firstPage);
                    cmd.Parameters.AddWithValue("$l", lastPage);
                    cmd.Parameters.AddWithValue("$m", model);
                    cmd.Parameters.AddWithValue("$v", PromptVersion);
                    cmd.Parameters.AddWithValue("$h", contentHash);
                    cmd.Parameters.AddWithValue("$c", content);
                    cmd.Parameters.AddWithValue("$w", wordCount);
                    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                    cmd.ExecuteNonQuery();
                }
            }
            catch
            {
                // best-effort
            }
        }

        private static SqliteConnection Connection()
        {
            if (_connection != null)
            {
                return _connection;
            }

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Avalanche", "AI");
            Directory.CreateDirectory(dir);
            // Default Timeout=2 bounds lock waits: vector_index.db is shared with the chat
            // indexer, and a cache miss must never sit behind its write lock for long.
            var conn = new SqliteConnection(
                "Data Source=" + Path.Combine(dir, "vector_index.db") + ";Cache=Shared;Default Timeout=2");
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS summary_cache(" +
                    "document_id TEXT NOT NULL, first_page INTEGER NOT NULL, last_page INTEGER NOT NULL, " +
                    "model TEXT NOT NULL, prompt_version INTEGER NOT NULL, content_hash TEXT NOT NULL, " +
                    "content TEXT NOT NULL, word_count INTEGER NOT NULL, created_utc TEXT NOT NULL, " +
                    "PRIMARY KEY(document_id, first_page, last_page, model, prompt_version))";
                cmd.ExecuteNonQuery();
            }

            _connection = conn;
            return _connection;
        }
    }
}
