using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// SQLite-based persistent lexical index for document chunks (FTS5/BM25).
    /// The index is a disposable cache rebuilt from the PDF, so schema drift
    /// from older builds is healed by dropping and recreating the tables.
    /// </summary>
    public sealed class VectorIndex : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _connection;
        private bool _disposed;

        // Expected column sets. Older builds created different schemas (for
        // example the embedding columns that have since been removed), and
        // "CREATE TABLE IF NOT EXISTS" cannot migrate an existing table - a
        // stale schema made every INSERT fail with "table documents has no
        // column named ...". When the on-disk columns do not match exactly,
        // the tables are dropped and rebuilt from scratch instead.
        private static readonly string[] DocumentsColumns =
        {
            "id", "file_path", "file_size", "last_write_time", "content_hash",
            "page_count", "created_at", "updated_at"
        };

        private static readonly string[] ChunksColumns =
        {
            "id", "document_id", "chunk_index", "text", "page_indices", "word_ranges",
            "pdf_coords", "char_offset", "lexical_tokens", "page_sizes",
            "page_rotations", "crop_boxes", "created_at"
        };

        public VectorIndex(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));

            var directory = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            _connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            _connection.Open();

            InitializeSchema();
        }

        private void InitializeSchema()
        {
            using var pragma = _connection.CreateCommand();
            pragma.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;
                PRAGMA mmap_size = 268435456;
                PRAGMA page_size = 4096;
            ";
            pragma.ExecuteNonQuery();

            if (!SchemaMatches())
                DropAllTables();

            CreateTables();
        }

        private void CreateTables()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS documents (
                    id TEXT PRIMARY KEY,
                    file_path TEXT NOT NULL,
                    file_size INTEGER NOT NULL,
                    last_write_time INTEGER NOT NULL,
                    content_hash TEXT NOT NULL,
                    page_count INTEGER NOT NULL,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS chunks (
                    id TEXT PRIMARY KEY,
                    document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                    chunk_index INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    page_indices TEXT NOT NULL, -- JSON array of page indices
                    word_ranges TEXT NOT NULL, -- JSON array of [start, end] per page
                    pdf_coords TEXT NOT NULL, -- JSON array of [left, bottom, right, top] per page
                    char_offset INTEGER NOT NULL,
                    lexical_tokens TEXT NOT NULL, -- JSON array of tokens
                    page_sizes TEXT NOT NULL DEFAULT '[]', -- JSON [[width, height] per page]
                    page_rotations TEXT NOT NULL DEFAULT '[]', -- JSON [degrees per page]
                    crop_boxes TEXT NOT NULL DEFAULT '[]', -- JSON [[l, b, r, t] per page]
                    created_at INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_chunks_document ON chunks(document_id);

                -- FTS5 virtual table for full-text search
                CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
                    chunk_id UNINDEXED,
                    document_id UNINDEXED,
                    text,
                    tokenize = 'porter unicode61'
                );

                CREATE TRIGGER IF NOT EXISTS chunks_after_insert
                AFTER INSERT ON chunks BEGIN
                    INSERT INTO chunks_fts (chunk_id, document_id, text)
                    VALUES (NEW.id, NEW.document_id, NEW.text);
                END;

                CREATE TRIGGER IF NOT EXISTS chunks_after_delete
                AFTER DELETE ON chunks BEGIN
                    DELETE FROM chunks_fts WHERE chunk_id = OLD.id;
                END;

                CREATE TRIGGER IF NOT EXISTS chunks_after_update
                AFTER UPDATE ON chunks BEGIN
                    DELETE FROM chunks_fts WHERE chunk_id = OLD.id;
                    INSERT INTO chunks_fts (chunk_id, document_id, text)
                    VALUES (NEW.id, NEW.document_id, NEW.text);
                END;
            ";
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// True when every existing table has exactly the expected columns.
        /// Absent tables are fine - CreateTables creates them afterwards.
        /// </summary>
        private bool SchemaMatches()
        {
            var documentsColumns = GetTableColumns("documents");
            if (documentsColumns.Count > 0 && !ColumnsMatch(documentsColumns, DocumentsColumns))
                return false;

            var chunksColumns = GetTableColumns("chunks");
            if (chunksColumns.Count > 0 && !ColumnsMatch(chunksColumns, ChunksColumns))
                return false;

            return true;
        }

        private static bool ColumnsMatch(HashSet<string> existing, string[] expected)
        {
            if (existing.Count != expected.Length)
                return false;
            foreach (var column in expected)
            {
                if (!existing.Contains(column))
                    return false;
            }
            return true;
        }

        private HashSet<string> GetTableColumns(string table)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
            return columns;
        }

        private void DropAllTables()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                DROP TRIGGER IF EXISTS chunks_after_insert;
                DROP TRIGGER IF EXISTS chunks_after_delete;
                DROP TRIGGER IF EXISTS chunks_after_update;
                DROP TABLE IF EXISTS chunks;
                DROP TABLE IF EXISTS documents;
                DROP TABLE IF EXISTS chunks_fts;
            ";
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Upserts a document record.
        /// </summary>
        public void UpsertDocument(DocumentIndex doc, SqliteTransaction? transaction = null)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO documents (id, file_path, file_size, last_write_time, content_hash, page_count, created_at, updated_at)
                VALUES ($id, $file_path, $file_size, $last_write_time, $content_hash, $page_count, $created_at, $updated_at)
                ON CONFLICT(id) DO UPDATE SET
                    file_path = $file_path,
                    file_size = $file_size,
                    last_write_time = $last_write_time,
                    content_hash = $content_hash,
                    page_count = $page_count,
                    updated_at = $updated_at;
            ";
            cmd.Parameters.AddWithValue("$id", doc.DocumentId);
            cmd.Parameters.AddWithValue("$file_path", doc.FilePath);
            cmd.Parameters.AddWithValue("$file_size", doc.FileSize);
            cmd.Parameters.AddWithValue("$last_write_time", doc.LastWriteTime);
            cmd.Parameters.AddWithValue("$content_hash", doc.ContentHash);
            cmd.Parameters.AddWithValue("$page_count", doc.PageCount);
            cmd.Parameters.AddWithValue("$created_at", doc.CreatedAt);
            cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Gets a document by ID.
        /// </summary>
        public DocumentIndex? GetDocument(string documentId)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, file_path, file_size, last_write_time, content_hash, page_count, created_at, updated_at FROM documents WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", documentId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            return new DocumentIndex
            {
                DocumentId = reader.GetString(0),
                FilePath = reader.GetString(1),
                FileSize = reader.GetInt64(2),
                LastWriteTime = reader.GetInt64(3),
                ContentHash = reader.GetString(4),
                PageCount = reader.GetInt32(5),
                CreatedAt = reader.GetInt64(6),
                UpdatedAt = reader.GetInt64(7)
            };
        }

        /// <summary>
        /// Checks if document exists and is unchanged.
        /// </summary>
        public bool IsDocumentCurrent(string documentId, long fileSize, long lastWriteTime, string contentHash)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT file_size, last_write_time, content_hash FROM documents WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", documentId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return false;

            return reader.GetInt64(0) == fileSize
                && reader.GetInt64(1) == lastWriteTime
                && reader.GetString(2) == contentHash;
        }

        /// <summary>
        /// Bulk inserts chunks.
        /// </summary>
        public void BulkInsertChunks(IEnumerable<DocumentChunk> chunks, SqliteTransaction? transaction = null)
        {
            bool ownTx = transaction is null;
            using var tx = ownTx ? _connection.BeginTransaction() : null;
            var effectiveTx = transaction ?? tx;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = effectiveTx;
                cmd.CommandText = @"
                    INSERT INTO chunks (id, document_id, chunk_index, text, page_indices, word_ranges, pdf_coords, char_offset, lexical_tokens, page_sizes, page_rotations, crop_boxes, created_at)
                    VALUES ($id, $document_id, $chunk_index, $text, $page_indices, $word_ranges, $pdf_coords, $char_offset, $lexical_tokens, $page_sizes, $page_rotations, $crop_boxes, $created_at)
                    ON CONFLICT(id) DO UPDATE SET
                        document_id = $document_id,
                        chunk_index = $chunk_index,
                        text = $text,
                        page_indices = $page_indices,
                        word_ranges = $word_ranges,
                        pdf_coords = $pdf_coords,
                        char_offset = $char_offset,
                        lexical_tokens = $lexical_tokens,
                        page_sizes = $page_sizes,
                        page_rotations = $page_rotations,
                        crop_boxes = $crop_boxes;
                ";

                foreach (var chunk in chunks)
                {
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$id", chunk.ChunkId);
                    cmd.Parameters.AddWithValue("$document_id", chunk.DocumentId);
                    cmd.Parameters.AddWithValue("$chunk_index", chunk.ChunkIndex);
                    cmd.Parameters.AddWithValue("$text", chunk.Text);
                    cmd.Parameters.AddWithValue("$page_indices", System.Text.Json.JsonSerializer.Serialize(chunk.PageIndices));
                    cmd.Parameters.AddWithValue("$word_ranges", System.Text.Json.JsonSerializer.Serialize(chunk.WordRanges));
                    cmd.Parameters.AddWithValue("$pdf_coords", System.Text.Json.JsonSerializer.Serialize(chunk.PdfCoordinates));
                    cmd.Parameters.AddWithValue("$char_offset", chunk.CharOffset);
                    cmd.Parameters.AddWithValue("$lexical_tokens", System.Text.Json.JsonSerializer.Serialize(chunk.LexicalTokens));
                    cmd.Parameters.AddWithValue("$page_sizes", System.Text.Json.JsonSerializer.Serialize(chunk.PageSizes));
                    cmd.Parameters.AddWithValue("$page_rotations", System.Text.Json.JsonSerializer.Serialize(chunk.PageRotations));
                    cmd.Parameters.AddWithValue("$crop_boxes", System.Text.Json.JsonSerializer.Serialize(chunk.CropBoxes));
                    cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    cmd.ExecuteNonQuery();
                }

                // Commit only when we own the transaction; when the caller
                // supplies one (PersistDocumentAtomic) it commits or rolls back.
                if (tx is not null) tx.Commit();
            }
            catch
            {
                if (tx is not null) tx.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Persists document + chunks as ONE transaction: the document row and
        /// its chunks become visible together, so a crash mid-persist can never
        /// leave a "current" document with zero chunks.
        /// </summary>
        public void PersistDocumentAtomic(DocumentIndex doc)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                DeleteDocumentChunks(doc.DocumentId, transaction);
                UpsertDocument(doc, transaction);
                BulkInsertChunks(doc.Chunks, transaction);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Deletes chunks + document rows of OTHER document ids that point at
        /// the same file path. Older builds keyed documents by path+size+mtime,
        /// so every file edit orphaned the previous chunks and the database only
        /// ever grew; this reclaims those rows on the next successful index.
        /// </summary>
        public void CleanupOrphanedDocumentRows(string filePath, string keepDocumentId)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM chunks WHERE document_id IN (SELECT id FROM documents WHERE file_path = $path AND id != $id)";
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.Parameters.AddWithValue("$id", keepDocumentId);
                cmd.ExecuteNonQuery();

                cmd.Parameters.Clear();
                cmd.CommandText = "DELETE FROM documents WHERE file_path = $path AND id != $id";
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.Parameters.AddWithValue("$id", keepDocumentId);
                cmd.ExecuteNonQuery();

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Loads all persisted chunks for a document, ordered by chunk index.
        /// </summary>
        public List<DocumentChunk> GetChunksForDocument(string documentId)
        {
            var chunks = new List<DocumentChunk>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, document_id, chunk_index, text, page_indices, word_ranges, pdf_coords, char_offset, lexical_tokens, page_sizes, page_rotations, crop_boxes FROM chunks WHERE document_id = $doc_id ORDER BY chunk_index";
            cmd.Parameters.AddWithValue("$doc_id", documentId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                chunks.Add(ReadChunkRow(reader));
            return chunks;
        }

        /// <summary>
        /// Performs lexical (full-text) search using FTS5. FTS5's bm25() returns
        /// NEGATIVE values (better matches are more negative), so the score is
        /// its negation: higher = better, always positive. The old 1/(1+bm25)
        /// mapping was non-monotonic with a division-by-zero pole at exactly -1,
        /// and min-max normalization + the MinScore filter then dropped the very
        /// hits the query was about. Rows come back best-first.
        /// </summary>
        public List<RetrievedChunk> LexicalSearch(string documentId, string query, int topK = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<RetrievedChunk>();

            var matchQuery = FtsQueryBuilder.Build(query);
            if (matchQuery is null)
                return new List<RetrievedChunk>();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT c.id, c.document_id, c.chunk_index, c.text, c.page_indices, c.word_ranges, c.pdf_coords, c.char_offset, c.lexical_tokens, c.page_sizes, c.page_rotations, c.crop_boxes,
                       bm25(chunks_fts) as rank
                FROM chunks_fts
                JOIN chunks c ON c.id = chunks_fts.chunk_id
                WHERE chunks_fts.document_id = $doc_id AND chunks_fts MATCH $query
                ORDER BY rank
                LIMIT $limit
            ";
            cmd.Parameters.AddWithValue("$doc_id", documentId);
            cmd.Parameters.AddWithValue("$query", matchQuery);
            cmd.Parameters.AddWithValue("$limit", topK);

            var results = new List<RetrievedChunk>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var chunk = ReadChunkRow(reader);
                float score = (float)(-reader.GetDouble(12)); // FTS5 bm25() is <= 0; negate it
                results.Add(new RetrievedChunk { Chunk = chunk, Score = score });
            }

            return results;
        }

        /// <summary>
        /// Deletes all chunks for a document (for re-indexing).
        /// </summary>
        public void DeleteDocumentChunks(string documentId, SqliteTransaction? transaction = null)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM chunks WHERE document_id = $doc_id";
            cmd.Parameters.AddWithValue("$doc_id", documentId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Deletes a document and its chunks.
        /// </summary>
        public void DeleteDocument(string documentId)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM chunks WHERE document_id = $doc_id";
                cmd.Parameters.AddWithValue("$doc_id", documentId);
                cmd.ExecuteNonQuery();

                cmd.CommandText = "DELETE FROM documents WHERE id = $doc_id";
                cmd.ExecuteNonQuery();

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public static void NormalizeScores(List<RetrievedChunk> results)
        {
            if (results.Count == 0) return;
            float max = results.Max(r => r.Score);
            float min = results.Min(r => r.Score);
            if (max <= min) return;
            foreach (var r in results)
                r.Score = (r.Score - min) / (max - min);
        }

        /// <summary>Reads a chunk row in the fixed column order shared by
        /// GetChunksForDocument and LexicalSearch. The per-page geometry arrays
        /// are the stored source of truth; the legacy first-page fields are
        /// mapped from them so older consumers keep working.</summary>
        private DocumentChunk ReadChunkRow(SqliteDataReader reader)
        {
            var chunk = new DocumentChunk
            {
                ChunkId = reader.GetString(0),
                DocumentId = reader.GetString(1),
                ChunkIndex = reader.GetInt32(2),
                Text = reader.GetString(3),
                PageIndices = System.Text.Json.JsonSerializer.Deserialize<List<int>>(reader.GetString(4)) ?? new(),
                WordRanges = System.Text.Json.JsonSerializer.Deserialize<List<int[]>>(reader.GetString(5)) ?? new(),
                PdfCoordinates = System.Text.Json.JsonSerializer.Deserialize<List<float[]>>(reader.GetString(6)) ?? new(),
                CharOffset = reader.GetInt64(7),
                LexicalTokens = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? new(),
                PageSizes = System.Text.Json.JsonSerializer.Deserialize<List<float[]>>(reader.GetString(9)) ?? new(),
                PageRotations = System.Text.Json.JsonSerializer.Deserialize<List<int>>(reader.GetString(10)) ?? new(),
                CropBoxes = System.Text.Json.JsonSerializer.Deserialize<List<float[]>>(reader.GetString(11)) ?? new()
            };
            chunk.PageWidth = chunk.PageSizes.Count > 0 && chunk.PageSizes[0].Length == 2 ? chunk.PageSizes[0][0] : 0;
            chunk.PageHeight = chunk.PageSizes.Count > 0 && chunk.PageSizes[0].Length == 2 ? chunk.PageSizes[0][1] : 0;
            chunk.PageRotation = chunk.PageRotations.Count > 0 ? chunk.PageRotations[0] : 0;
            chunk.CropBox = chunk.CropBoxes.Count > 0 && chunk.CropBoxes[0].Length == 4 ? chunk.CropBoxes[0] : null;
            return chunk;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _connection?.Dispose();
                _disposed = true;
            }
        }
    }
}
