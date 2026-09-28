using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Avalanche.Features.AI
{
    /// <summary>Why an index build refused to produce a usable index.</summary>
    public enum AiIndexingFailure
    {
        NoTextLayer,
        PasswordProtected,
        Unreadable
    }

    /// <summary>A typed indexing failure the chat view model maps to a friendly,
    /// localized message instead of leaking raw PdfPig exception text.</summary>
    public sealed class AiIndexingException : Exception
    {
        public AiIndexingFailure Reason { get; }

        public AiIndexingException(AiIndexingFailure reason, string message)
            : base(message)
        {
            Reason = reason;
        }
    }

    /// <summary>
    /// Creates and manages persistent lexical (FTS5/BM25) document indexes.
    /// No embedding model is used or required.
    /// </summary>
    public sealed class DocumentIndexer
    {
        private readonly VectorIndex _vectorIndex;
        private readonly IndexingOptions _options;

        public DocumentIndexer(VectorIndex vectorIndex, IndexingOptions? options = null)
        {
            _vectorIndex = vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
            _options = options ?? new IndexingOptions();
        }

        /// <summary>
        /// Stable, content-independent document key: a hash of the normalized
        /// full path ONLY. Older builds mixed size and mtime into the id, so
        /// every file edit minted a new id and orphaned the previous chunks -
        /// the database only ever grew. With a stable id an edited file
        /// re-indexes under the SAME id and replaces its chunks; size, mtime
        /// and the content hash decide WHETHER re-indexing is needed.
        /// </summary>
        public static string ComputeDocumentId(string filePath)
        {
            // Windows paths are case-insensitive; normalizing casing keeps the
            // id stable when the same file is opened through different casing.
            var identity = Path.GetFullPath(filePath).ToLowerInvariant();
            return $"doc_{ComputeSha256(identity)}";
        }

        /// <summary>
        /// Creates or loads a document index. If the document is unchanged, loads from cache.
        /// All file I/O (including the SHA-256 content hash) runs on a worker
        /// thread - it previously ran on the caller (UI) thread before the first
        /// await, freezing the window on every open of a large PDF.
        /// </summary>
        public async Task<DocumentIndex> CreateOrLoadIndexAsync(string filePath, IProgress<IndexingProgress>? progress = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"PDF not found: {filePath}");

            var documentId = ComputeDocumentId(filePath);

            return await Task.Run(() =>
            {
                var fileInfo = new FileInfo(filePath);
                var contentHash = ComputeContentHash(filePath);

                var existingDoc = _vectorIndex.GetDocument(documentId);
                if (existingDoc != null
                    && _vectorIndex.IsDocumentCurrent(documentId, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks, contentHash))
                {
                    var chunks = _vectorIndex.GetChunksForDocument(documentId);

                    // Self-heal the crash legacy of the pre-atomic persist: the
                    // document row could be written, then the process died before
                    // its chunks - leaving a "current" document with zero chunks
                    // that never matched anything, forever. Rebuild instead.
                    if (chunks.Count > 0)
                    {
                        existingDoc.Chunks = chunks;
                        progress?.Report(new IndexingProgress { Stage = IndexingStage.Loaded, Progress = 1.0, Message = "Loaded from cache" });
                        return existingDoc;
                    }
                }

                progress?.Report(new IndexingProgress { Stage = IndexingStage.Extracting, Progress = 0.0, Message = "Extracting text..." });

                var doc = BuildIndex(filePath, documentId, contentHash, fileInfo, progress);

                progress?.Report(new IndexingProgress { Stage = IndexingStage.Persisting, Progress = 0.9, Message = "Persisting index..." });
                PersistIndex(doc);

                // Reclaim rows orphaned by the old path+size+mtime id scheme.
                try { _vectorIndex.CleanupOrphanedDocumentRows(filePath, documentId); }
                catch { /* reclaiming is best-effort; never fail indexing over it */ }

                progress?.Report(new IndexingProgress { Stage = IndexingStage.Complete, Progress = 1.0, Message = "Indexing complete" });
                return doc;
            });
        }

        /// <summary>
        /// Builds a fresh index from the PDF. Extraction streams page by page
        /// into the chunker - the previous build held every word of the whole
        /// document in memory before chunking even started.
        /// </summary>
        private DocumentIndex BuildIndex(string filePath, string documentId, string contentHash, FileInfo fileInfo, IProgress<IndexingProgress>? progress)
        {
            List<DocumentChunk> chunks;
            int pageCount;

            try
            {
                using var pdfDoc = PdfDocument.Open(filePath);
                pageCount = pdfDoc.NumberOfPages;

                // Page geometry is captured once per page; chunks map onto it
                // afterwards. Chunks can span pages, and different pages of one
                // document may differ in size/rotation, so it is stored per page.
                var geometry = new Dictionary<int, (float Width, float Height, int Rotation, float[] CropBox)>(pageCount);

                var chunker = new DocumentChunker(new ChunkerOptions
                {
                    TargetChunkSize = _options.TargetChunkSize,
                    MaxChunkSize = _options.MaxChunkSize,
                    MinChunkWords = _options.MinChunkWords
                });

                int emptyPages = 0;

                for (int pi = 0; pi < pageCount; pi++)
                {
                    var page = pdfDoc.GetPage(pi + 1);
                    var rawWords = page.GetWords()
                        .Select(w => new IndexedWord(w.Text, w.BoundingBox.Left, w.BoundingBox.Bottom,
                            w.BoundingBox.Right, w.BoundingBox.Top))
                        .ToList();

                    var crop = page.CropBox.Bounds;
                    geometry[pi] = (
                        (float)page.Width,
                        (float)page.Height,
                        page.Rotation.Value,
                        new[] { (float)crop.Left, (float)crop.Bottom, (float)crop.Right, (float)crop.Top }
                    );

                    if (rawWords.Count == 0)
                    {
                        emptyPages++;
                        continue;
                    }

                    // Column-aware reading order + paragraph/heading structure
                    // derived from geometry (PdfPig words never contain newlines).
                    var stream = PageWordStreamBuilder.Build(rawWords, page.Width, page.Height);
                    chunker.AppendPage(pi, stream);

                    if (pi % 10 == 0)
                    {
                        progress?.Report(new IndexingProgress
                        {
                            Stage = IndexingStage.Extracting,
                            Progress = (double)pi / pageCount * 0.9,
                            Message = $"Extracting page {pi + 1}/{pageCount}"
                        });
                    }
                }

                if (pageCount > 0 && emptyPages == pageCount)
                {
                    // Every page was empty: a scanned document with no text
                    // layer. Surface it instead of "indexing complete" with
                    // zero chunks that can never match a question.
                    // TODO(ai.txt #10): OCR path - rasterize text-less pages and
                    // feed Services/OcrService output into the chunker. The app's
                    // OCR engine needs native bootstrap + language packs and a
                    // page-budget policy for 1000-page scans, so for now the user
                    // gets the explicit "no text layer" message instead of
                    // silently useless indexes.
                    throw new AiIndexingException(
                        AiIndexingFailure.NoTextLayer,
                        "The document has no extractable text layer (all pages empty).");
                }

                chunks = chunker.Finish().Select(a => ToDocumentChunk(a, documentId, geometry)).ToList();
            }
            catch (AiIndexingException)
            {
                throw;
            }
            catch (Exception ex) when (IsPasswordFailure(ex))
            {
                throw new AiIndexingException(
                    AiIndexingFailure.PasswordProtected,
                    "The PDF is password-protected and PdfPig could not open it.");
            }
            catch (Exception ex) when (IsEncryptedOrBroken(ex))
            {
                throw new AiIndexingException(
                    AiIndexingFailure.Unreadable,
                    $"The PDF could not be parsed for indexing: {ex.Message}");
            }

            return new DocumentIndex
            {
                DocumentId = documentId,
                FilePath = filePath,
                FileSize = fileInfo.Length,
                LastWriteTime = fileInfo.LastWriteTimeUtc.Ticks,
                ContentHash = contentHash,
                PageCount = pageCount,
                Chunks = chunks,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        private static bool IsPasswordFailure(Exception ex)
        {
            var msg = ex.Message ?? "";
            return msg.Contains("password", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Password", StringComparison.Ordinal);
        }

        private static bool IsEncryptedOrBroken(Exception ex)
        {
            var msg = ex.Message ?? "";
            return msg.Contains("encrypt", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("corrupt", StringComparison.OrdinalIgnoreCase);
        }

        private DocumentChunk ToDocumentChunk(
            AssembledChunk assembled,
            string documentId,
            Dictionary<int, (float Width, float Height, int Rotation, float[] CropBox)> geometry)
        {
            var lexicalTokens = assembled.Text.ToLowerInvariant()
                .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 1)
                .Distinct()
                .ToList();

            var chunk = new DocumentChunk
            {
                ChunkId = $"chunk_{Guid.NewGuid():N}",
                DocumentId = documentId,
                ChunkIndex = assembled.ChunkIndex,
                Text = assembled.Text,
                PageIndices = new List<int>(assembled.PageIndices),
                WordRanges = assembled.WordRanges.Select(r => (int[])r.Clone()).ToList(),
                PdfCoordinates = assembled.PdfCoordinates.Select(r => (float[])r.Clone()).ToList(),
                CharOffset = assembled.CharOffset,
                LexicalTokens = lexicalTokens,
                SectionHeading = assembled.SectionHeading
            };

            foreach (var pi in assembled.PageIndices)
            {
                if (geometry.TryGetValue(pi, out var geo))
                {
                    chunk.PageSizes.Add(new[] { geo.Width, geo.Height });
                    chunk.PageRotations.Add(geo.Rotation);
                    chunk.CropBoxes.Add((float[])geo.CropBox.Clone());
                }
                else
                {
                    chunk.PageSizes.Add(new[] { 0f, 0f });
                    chunk.PageRotations.Add(0);
                    chunk.CropBoxes.Add(new[] { 0f, 0f, 0f, 0f });
                }
            }

            if (chunk.PageSizes.Count > 0)
            {
                chunk.PageWidth = chunk.PageSizes[0][0];
                chunk.PageHeight = chunk.PageSizes[0][1];
                chunk.PageRotation = chunk.PageRotations[0];
                chunk.CropBox = chunk.CropBoxes[0];
            }

            return chunk;
        }

        /// <summary>
        /// Persists document and chunks in ONE transaction (previously the
        /// document row was written first and the chunks after; a crash in
        /// between left a "current" document with zero chunks - permanently).
        /// </summary>
        private void PersistIndex(DocumentIndex doc)
        {
            _vectorIndex.PersistDocumentAtomic(doc);
        }

        private static string ComputeContentHash(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }

        private static string ComputeSha256(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToHexString(hash).Substring(0, 16);
        }
    }

    /// <summary>
    /// Configuration options for indexing.
    /// </summary>
    public sealed class IndexingOptions
    {
        public int TargetChunkSize { get; set; } = 500;
        public int MaxChunkSize { get; set; } = 800;
        public int MinChunkSize { get; set; } = 100;
        public int MinChunkWords { get; set; } = 20;
    }

    /// <summary>
    /// Progress reporting for indexing operations.
    /// </summary>
    public sealed class IndexingProgress
    {
        public IndexingStage Stage { get; set; }
        public double Progress { get; set; }  // 0.0 to 1.0
        public string Message { get; set; } = "";
    }

    public enum IndexingStage
    {
        Extracting,
        Chunking,
        Persisting,
        Loaded,
        Complete
    }
}