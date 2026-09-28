using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Creates and manages persistent document indexes with embeddings.
    /// </summary>
    public sealed class DocumentIndexer
    {
        private readonly IEmbeddingProvider _embeddingProvider;
        private readonly VectorIndex _vectorIndex;
        private readonly IndexingOptions _options;
        private readonly EmbeddingProviderConfig _embConfig;

        public DocumentIndexer(IEmbeddingProvider embeddingProvider, VectorIndex vectorIndex, EmbeddingProviderConfig embConfig, IndexingOptions? options = null)
        {
            _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
            _vectorIndex = vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
            _embConfig = embConfig ?? throw new ArgumentNullException(nameof(embConfig));
            _options = options ?? new IndexingOptions();
        }

        /// <summary>
        /// Creates or loads a document index. If the document is unchanged, loads from cache.
        /// </summary>
        public async Task<DocumentIndex> CreateOrLoadIndexAsync(string filePath, IProgress<IndexingProgress>? progress = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"PDF not found: {filePath}");

            var fileInfo = new FileInfo(filePath);
            var documentId = ComputeDocumentId(filePath);
            
            // Check if we have a current index with compatible embedding model
            var (embModelName, embDimension) = _embeddingProvider.GetModelInfo();
            var existingDoc = _vectorIndex.GetDocument(documentId);
            if (existingDoc != null && _vectorIndex.IsDocumentCurrent(documentId, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks, "", 
                _embConfig.EmbeddingModelName, _embConfig.EmbeddingDimension))
            {
                // Load existing chunks
                var chunks = LoadChunksForDocument(documentId);
                existingDoc.Chunks = chunks;
                progress?.Report(new IndexingProgress { Stage = IndexingStage.Loaded, Progress = 1.0, Message = "Loaded from cache" });
                return existingDoc;
            }

            // Need to (re)index
            progress?.Report(new IndexingProgress { Stage = IndexingStage.Extracting, Progress = 0.0, Message = "Extracting text..." });
            
            var doc = await BuildIndexAsync(filePath, documentId, fileInfo, progress);
            
            // Persist
            progress?.Report(new IndexingProgress { Stage = IndexingStage.Persisting, Progress = 0.9, Message = "Persisting index..." });
            PersistIndex(doc);
            
            progress?.Report(new IndexingProgress { Stage = IndexingStage.Complete, Progress = 1.0, Message = "Indexing complete" });
            return doc;
        }

        /// <summary>
        /// Builds a fresh index from the PDF.
        /// </summary>
        private async Task<DocumentIndex> BuildIndexAsync(string filePath, string documentId, FileInfo fileInfo, IProgress<IndexingProgress>? progress)
        {
            var allChunks = new List<DocumentChunk>();
            var (embModelName, embDimension) = _embeddingProvider.GetModelInfo();

            await Task.Run(() =>
            {
                using var pdfDoc = PdfDocument.Open(filePath);
                int pageCount = pdfDoc.NumberOfPages;

                var allWords = new List<(int pageIndex, Word word)>();

                // Extract all words with page info
                for (int pi = 0; pi < pageCount; pi++)
                {
                    var page = pdfDoc.GetPage(pi + 1);
                    var words = page.GetWords().ToList();
                    foreach (var word in words)
                    {
                        allWords.Add((pi, word));
                    }

                    // Report progress
                    if (progress != null && pi % 10 == 0)
                    {
                        progress?.Report(new IndexingProgress 
                        { 
                            Stage = IndexingStage.Extracting, 
                            Progress = (double)pi / pageCount * 0.3,
                            Message = $"Extracting page {pi + 1}/{pageCount}" 
                        });
                    }
                }

                // Create intelligent chunks
                var chunks = CreateChunks(allWords, pageCount, pdfDoc);
                
                // Generate embeddings
                if (progress != null)
                    progress?.Report(new IndexingProgress { Stage = IndexingStage.Embedding, Progress = 0.5, Message = "Generating embeddings..." });

                // Note: We need to call the async method, but we're in Task.Run
                // For now, use GetAwaiter().GetResult() but in the future this should be fully async
                GenerateEmbeddings(chunks);
                
                allChunks.AddRange(chunks);
            });

            var doc = new DocumentIndex
            {
                DocumentId = documentId,
                FilePath = filePath,
                FileSize = new FileInfo(filePath).Length,
                LastWriteTime = new FileInfo(filePath).LastWriteTimeUtc.Ticks,
                ContentHash = ComputeContentHash(filePath),
                PageCount = new FileInfo(filePath).Length > 0 ? GetPageCount(filePath) : 0,
                Chunks = allChunks,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                EmbeddingModelName = _embeddingProvider.GetModelInfo().ModelName,
                EmbeddingDimension = _embeddingProvider.GetModelInfo().Dimension
            };

            return doc;
        }

        private List<DocumentChunk> LoadChunksForDocument(string documentId)
        {
            // This would load from the vector index
            // For now, return empty - actual implementation would query the DB
            return new List<DocumentChunk>();
        }

        private void PersistIndex(DocumentIndex doc)
        {
            _vectorIndex.UpsertDocument(doc);
            _vectorIndex.BulkInsertChunks(doc.Chunks);
        }

        private List<DocumentChunk> CreateChunks(List<(int pageIndex, Word word)> allWords, int pageCount, UglyToad.PdfPig.PdfDocument pdfDoc)
        {
            var chunks = new List<DocumentChunk>();
            var currentChunkWords = new List<(int pageIndex, Word word)>();
            int chunkIndex = 0;
            long charOffset = 0;

            for (int i = 0; i < allWords.Count; i++)
            {
                var (pageIndex, word) = allWords[i];
                currentChunkWords.Add((pageIndex, word));

                // Calculate current chunk length
                int currentLength = currentChunkWords.Sum(w => w.word.Text.Length + 1);

                bool shouldBreak = false;

                // Check for natural break points
                if (currentLength >= _options.TargetChunkSize)
                {
                    int breakIndex = FindNaturalBreak(currentChunkWords, i, allWords);
                    if (breakIndex >= 0)
                    {
                        var chunkWords = currentChunkWords.GetRange(0, breakIndex + 1);
                        var chunk = CreateChunk(chunkWords, chunkIndex++, ref charOffset, pdfDoc);
                        if (chunk != null) chunks.Add(chunk);
                        currentChunkWords.RemoveRange(0, breakIndex + 1);
                        shouldBreak = true;
                    }
                    else if (currentLength >= _options.MaxChunkSize)
                    {
                        // Force break
                        var chunk = CreateChunk(currentChunkWords, chunkIndex++, ref charOffset);
                        if (chunk != null) chunks.Add(chunk);
                        currentChunkWords.Clear();
                        shouldBreak = true;
                    }
                }

                // Check for page boundary
                if (!shouldBreak && i + 1 < allWords.Count)
                {
                    var nextPage = allWords[i + 1].pageIndex;
                    if (nextPage != pageIndex)
                    {
                        if (currentChunkWords.Count >= _options.MinChunkWords)
                        {
                            var chunk = CreateChunk(currentChunkWords, chunkIndex++, ref charOffset, pdfDoc);
                            if (chunk != null) chunks.Add(chunk);
                            currentChunkWords.Clear();
                        }
                    }
                }
            }

            // Don't forget the last chunk
            if (currentChunkWords.Count > 0)
            {
                var chunk = CreateChunk(currentChunkWords, chunkIndex, ref charOffset, pdfDoc);
                if (chunk != null) chunks.Add(chunk);
            }

            return chunks;
        }

        private DocumentChunk? CreateChunk(List<(int pageIndex, Word word)> words, int chunkIndex, ref long charOffset, UglyToad.PdfPig.PdfDocument pdfDoc)
        {
            if (words.Count == 0) return null;

            // Combine text
            var text = string.Join(" ", words.Select(w => w.word.Text));
            if (string.IsNullOrWhiteSpace(text)) return null;

            // Group words by page
            var pageGroups = words.GroupBy(w => w.pageIndex).OrderBy(g => g.Key).ToList();
            
            var pageIndices = new List<int>();
            var wordRanges = new List<int[]>();
            var pdfCoordinates = new List<float[]>();
            
            // Get page info for the first page (primary page)
            float pageWidth = 0, pageHeight = 0;
            int pageRotation = 0;
            float[]? cropBox = null;
            
            if (pageGroups.Count > 0)
            {
                var firstPageIndex = pageGroups[0].Key;
                var page = pdfDoc.GetPage(firstPageIndex + 1);
                pageWidth = page.Width;
                pageHeight = page.Height;
                pageRotation = page.Rotation;
                var crop = page.CropBox;
                cropBox = new float[] { (float)crop.Left, (float)crop.Bottom, (float)crop.Right, (float)crop.Top };
            }

            int wordOffset = 0;
            foreach (var group in pageGroups)
            {
                var pageWords = group.ToList();
                int startWord = wordOffset;
                int endWord = wordOffset + pageWords.Count - 1;
                
                pageIndices.Add(group.Key);
                wordRanges.Add(new[] { startWord, endWord });
                
                // Calculate bounding box for this page's portion
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                
                foreach (var (_, word) in pageWords)
                {
                    var bb = word.BoundingBox;
                    minX = Math.Min(minX, bb.Left);
                    minY = Math.Min(minY, bb.Bottom);
                    maxX = Math.Max(maxX, bb.Right);
                    maxY = Math.Max(maxY, bb.Top);
                }
                
                pdfCoordinates.Add(new float[] 
                { 
                    (float)minX, (float)minY, (float)maxX, (float)maxY 
                });
                
                wordOffset += pageWords.Count;
            }

            // Lexical tokens for BM25
            var lexicalTokens = text.ToLowerInvariant()
                .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 1)
                .Distinct()
                .ToList();

            var chunk = new DocumentChunk
            {
                ChunkId = $"chunk_{Guid.NewGuid():N}",
                DocumentId = "", // Will be set by caller
                ChunkIndex = chunkIndex,
                Text = text,
                PageIndices = pageIndices,
                WordRanges = wordRanges,
                PdfCoordinates = pdfCoordinates,
                CharOffset = charOffset,
                LexicalTokens = lexicalTokens,
                PageWidth = pageWidth,
                PageHeight = pageHeight,
                PageRotation = pageRotation,
                CropBox = cropBox
            };

            charOffset += text.Length + 1;
            return chunk;
        }

        private int FindNaturalBreak(List<(int pageIndex, Word word)> chunkWords, int currentIndex, List<(int pageIndex, Word word)> allWords)
        {
            // Look backwards from the end for natural breaks
            for (int i = chunkWords.Count - 1; i >= 0; i--)
            {
                var text = chunkWords[i].word.Text;

                // Paragraph break
                if (text.Contains("\n\n") || text.EndsWith("\n"))
                    return i;

                // Sentence end with reasonable length
                if (text.EndsWith(".") || text.EndsWith("!") || text.EndsWith("?") || text.EndsWith(":"))
                {
                    if (text.Length > 2 && char.IsUpper(text[0]) && text.Length < 100)
                        return i;
                }

                // Heading-like
                if (text.Length < 80 && text.Length > 5)
                {
                    bool isTitleCase = text.Split(' ').All(w => w.Length == 0 || char.IsUpper(w[0]));
                    if (isTitleCase)
                        return i;
                }
            }

            return -1;
        }

        private async Task GenerateEmbeddingsAsync(List<DocumentChunk> chunks, EmbeddingProviderConfig embConfig)
        {
            if (chunks.Count == 0) return;

            // Apply query prefix for retrieval queries (done in the embedding provider)
            // The embedding provider handles document prefix internally
            var texts = chunks.Select(c => c.Text).ToList();
            var embeddings = await _embeddingProvider.GenerateEmbeddingsAsync(texts);

            for (int i = 0; i < chunks.Count && i < embeddings.Length; i++)
            {
                chunks[i].Embedding = embeddings[i];
            }
        }

        private string ComputeDocumentId(string filePath)
        {
            var info = new FileInfo(filePath);
            return $"doc_{ComputeSha256($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")}";
        }

        private string ComputeContentHash(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }

        private string ComputeSha256(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToHexString(hash).Substring(0, 16);
        }

        private int GetPageCount(string filePath)
        {
            using var doc = PdfDocument.Open(filePath);
            return doc.NumberOfPages;
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
        public bool GenerateEmbeddings { get; set; } = true;
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
        Embedding,
        Persisting,
        Loaded,
        Complete
    }
}