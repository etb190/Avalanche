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
    /// SQLite-based persistent vector index for document embeddings.
    /// Stores chunk metadata, embeddings, and supports hybrid search.
    /// </summary>
    public sealed class VectorIndex : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _connection;
        private bool _disposed;

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
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;
                PRAGMA mmap_size = 268435456;
                PRAGMA page_size = 4096;

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
                    embedding BLOB, -- Binary float32 array
                    lexical_tokens TEXT NOT NULL, -- JSON array of tokens
                    created_at INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_chunks_document ON chunks(document_id);
                CREATE INDEX IF NOT EXISTS idx_chunks_embedding ON chunks(document_id, id) WHERE embedding IS NOT NULL;

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
        /// Upserts a document record.
        /// </summary>
        public void UpsertDocument(DocumentIndex doc)
        {
            using var cmd = _connection.CreateCommand();
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
            cmd.CommandText = "SELECT * FROM documents WHERE id = $id";
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
                CreatedAt = reader.GetInt64(6)
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
        /// Bulk inserts chunks with embeddings.
        /// </summary>
        public void BulkInsertChunks(IEnumerable<DocumentChunk> chunks)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    INSERT INTO chunks (id, document_id, chunk_index, text, page_indices, word_ranges, pdf_coords, char_offset, embedding, lexical_tokens, created_at)
                    VALUES ($id, $document_id, $chunk_index, $text, $page_indices, $word_ranges, $pdf_coords, $char_offset, $embedding, $lexical_tokens, $created_at)
                    ON CONFLICT(id) DO UPDATE SET
                        document_id = $document_id,
                        chunk_index = $chunk_index,
                        text = $text,
                        page_indices = $page_indices,
                        word_ranges = $word_ranges,
                        pdf_coords = $pdf_coords,
                        char_offset = $char_offset,
                        embedding = $embedding,
                        lexical_tokens = $lexical_tokens;
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
                    cmd.Parameters.AddWithValue("$embedding", chunk.Embedding != null ? FloatArrayToBytes(chunk.Embedding) : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("$lexical_tokens", System.Text.Json.JsonSerializer.Serialize(chunk.LexicalTokens));
                    cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Performs semantic search using cosine similarity on embeddings.
        /// </summary>
        public List<RetrievedChunk> SemanticSearch(string documentId, float[] queryEmbedding, int topK = 20)
        {
            if (queryEmbedding == null || queryEmbedding.Length == 0)
                return new List<RetrievedChunk>();

            // Load all embeddings for the document and compute cosine similarity
            // For large datasets, this should use a proper ANN index (HNSW, etc.)
            // For now, we do linear scan which is fine for <100k chunks
            var candidates = new List<(DocumentChunk chunk, float similarity)>();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, document_id, chunk_index, text, page_indices, word_ranges, pdf_coords, char_offset, embedding, lexical_tokens FROM chunks WHERE document_id = $doc_id AND embedding IS NOT NULL";
            cmd.Parameters.AddWithValue("$doc_id", documentId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var embeddingBytes = reader.IsDBNull(8) ? null : (byte[])reader.GetValue(8);
                if (embeddingBytes == null || embeddingBytes.Length == 0)
                    continue;

                var embedding = BytesToFloatArray(embeddingBytes);
                if (embedding.Length != queryEmbedding.Length)
                    continue;

                float similarity = CosineSimilarity(queryEmbedding, embedding);
                if (similarity > 0.1f) // Threshold
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
                        Embedding = embedding,
                        LexicalTokens = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(9)) ?? new()
                    };
                    candidates.Add((chunk, similarity));
                }
            }

            return candidates
                .OrderByDescending(c => c.similarity)
                .Take(topK)
                .Select(c => new RetrievedChunk { Chunk = c.chunk, Score = c.similarity })
                .ToList();
        }

        /// <summary>
        /// Performs lexical (full-text) search using FTS5.
        /// </summary>
        public List<RetrievedChunk> LexicalSearch(string documentId, string query, int topK = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<RetrievedChunk>();

            // Escape FTS5 special characters
            var escapedQuery = EscapeFtsQuery(query);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT c.id, c.document_id, c.chunk_index, c.text, c.page_indices, c.word_ranges, c.pdf_coords, c.char_offset, c.embedding, c.lexical_tokens,
                       bm25(chunks_fts) as rank
                FROM chunks_fts
                JOIN chunks c ON c.id = chunks_fts.chunk_id
                WHERE chunks_fts.document_id = $doc_id AND chunks_fts MATCH $query
                ORDER BY rank
                LIMIT $limit
            ";
            cmd.Parameters.AddWithValue("$doc_id", documentId);
            cmd.Parameters.AddWithValue("$query", escapedQuery);
            cmd.Parameters.AddWithValue("$limit", topK);

            var results = new List<RetrievedChunk>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
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
                    Embedding = reader.IsDBNull(8) ? null : BytesToFloatArray((byte[])reader.GetValue(8)),
                    LexicalTokens = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(9)) ?? new()
                };

                float score = (float)(1.0 / (1.0 + reader.GetDouble(10))); // BM25 rank -> similarity
                results.Add(new RetrievedChunk { Chunk = chunk, Score = score });
            }

            return results;
        }

        /// <summary>
        /// Hybrid search combining lexical and semantic results.
        /// </summary>
        public List<RetrievedChunk> HybridSearch(string documentId, string query, float[] queryEmbedding, int topK = 10, float lexicalWeight = 0.4f, float semanticWeight = 0.6f)
        {
            // Get more candidates from each for better merge
            var lexicalResults = LexicalSearch(documentId, query, topK * 3);
            var semanticResults = SemanticSearch(documentId, queryEmbedding, topK * 3);

            // Normalize scores
            NormalizeScores(lexicalResults);
            NormalizeScores(semanticResults);

            // Merge by chunk ID
            var merged = new Dictionary<string, (DocumentChunk chunk, float lexicalScore, float semanticScore)>();

            foreach (var r in lexicalResults)
            {
                merged[r.Chunk.ChunkId] = (r.Chunk, r.Score, 0f);
            }

            foreach (var r in semanticResults)
            {
                if (merged.TryGetValue(r.Chunk.ChunkId, out var existing))
                {
                    merged[r.Chunk.ChunkId] = (existing.chunk, existing.lexicalScore, r.Score);
                }
                else
                {
                    merged[r.Chunk.ChunkId] = (r.Chunk, 0f, r.Score);
                }
            }

            // Compute hybrid scores
            var hybridResults = merged.Values
                .Select(m => new RetrievedChunk
                {
                    Chunk = m.chunk,
                    Score = (m.lexicalScore * lexicalWeight) + (m.semanticScore * semanticWeight)
                })
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .ToList();

            return hybridResults;
        }

        /// <summary>
        /// Deletes all chunks for a document (for re-indexing).
        /// </summary>
        public void DeleteDocumentChunks(string documentId)
        {
            using var cmd = _connection.CreateCommand();
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

        private static void NormalizeScores(List<RetrievedChunk> results)
        {
            if (results.Count == 0) return;
            float max = results.Max(r => r.Score);
            float min = results.Min(r => r.Score);
            if (max <= min) return;
            foreach (var r in results)
                r.Score = (r.Score - min) / (max - min);
        }

        private static float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0f;
            float dot = 0f, normA = 0f, normB = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }
            if (normA == 0f || normB == 0f) return 0f;
            return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
        }

        private static byte[] FloatArrayToBytes(float[] array)
        {
            var bytes = new byte[array.Length * 4];
            Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static float[] BytesToFloatArray(byte[] bytes)
        {
            var array = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, array, 0, bytes.Length);
            return array;
        }

        private static string EscapeFtsQuery(string query)
        {
            // Escape FTS5 special characters
            var escaped = query
                .Replace("\"", "\"\"")
                .Replace("'", "''")
                .Replace(":", " ")
                .Replace("-", " ")
                .Replace("(", " ")
                .Replace(")", " ")
                .Replace("[", " ")
                .Replace("]", " ")
                .Replace("{", " ")
                .Replace("}", " ")
                .Replace("^", " ")
                .Replace("*", " ")
                .Replace("?", " ");
            
            // Split into terms and wrap each in quotes for phrase matching
            var terms = escaped.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 1)
                return $"\"{terms[0]}\"";
            
            return string.Join(" OR ", terms.Select(t => $"\"{t}\""));
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