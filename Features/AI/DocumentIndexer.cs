using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Avalanche.Services;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Extracts and chunks PDF text for AI retrieval, preserving page and coordinate information.
    /// </summary>
    internal sealed class DocumentIndexer
    {
        private const int TargetChunkSize = 500;
        private const int MaxChunkSize = 800;
        private const int MinChunkSize = 100;

        /// <summary>
        /// Creates a document index from a PDF file.
        /// </summary>
        public static async Task<DocumentIndex> CreateIndexAsync(string filePath, string documentId)
        {
            var fileInfo = new FileInfo(filePath);
            var index = new DocumentIndex
            {
                DocumentId = documentId,
                FilePath = filePath,
                FileSize = fileInfo.Length,
                LastWriteTime = fileInfo.LastWriteTimeUtc.Ticks
            };

            await Task.Run(() =>
            {
                using var doc = PdfDocument.Open(filePath);
                index.PageCount = doc.NumberOfPages;

                var allWords = new List<(int pageIndex, Word word)>();

                for (int pi = 0; pi < doc.NumberOfPages; pi++)
                {
                    var page = doc.GetPage(pi + 1);
                    var words = page.GetWords().ToList();
                    foreach (var word in words)
                    {
                        allWords.Add((pi, word));
                    }
                }

                index.Chunks = CreateChunks(allWords);
            });

            return index;
        }

        /// <summary>
        /// Creates intelligent chunks from extracted words, preferring natural boundaries.
        /// </summary>
        private static List<DocumentChunk> CreateChunks(List<(int pageIndex, Word word)> allWords)
        {
            var chunks = new List<DocumentChunk>();
            var currentChunkWords = new List<(int pageIndex, Word word)>();
            int chunkCounter = 0;

            for (int i = 0; i < allWords.Count; i++)
            {
                var (pageIndex, word) = allWords[i];
                currentChunkWords.Add((pageIndex, word));

                bool shouldBreak = false;

                // Check if we've reached target size
                int currentLength = 0;
                foreach (var w in currentChunkWords)
                    currentLength += w.word.Text.Length + 1;

                if (currentLength >= TargetChunkSize)
                {
                    // Look for natural break points (paragraph, sentence, heading)
                    int breakIndex = FindNaturalBreak(currentChunkWords, i, allWords);
                    if (breakIndex >= 0)
                    {
                        var chunkWords = currentChunkWords.GetRange(0, breakIndex + 1);
                        chunks.Add(CreateChunk(chunkWords, chunkCounter++));
                        currentChunkWords.RemoveRange(0, breakIndex + 1);
                        shouldBreak = true;
                    }
                    else if (currentLength >= MaxChunkSize)
                    {
                        // Force break at max size
                        chunks.Add(CreateChunk(currentChunkWords, chunkCounter++));
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
                        // Page boundary - break here
                        if (currentChunkWords.Count >= MinChunkSize / 5) // approximate word count
                        {
                            chunks.Add(CreateChunk(currentChunkWords, chunkCounter++));
                            currentChunkWords.Clear();
                        }
                    }
                }
            }

            // Don't forget the last chunk
            if (currentChunkWords.Count > 0)
            {
                chunks.Add(CreateChunk(currentChunkWords, chunkCounter));
            }

            return chunks;
        }

        /// <summary>
        /// Finds a natural break point in the current chunk (paragraph, sentence end, etc.).
        /// </summary>
        private static int FindNaturalBreak(
            List<(int pageIndex, Word word)> chunkWords,
            int currentIndex,
            List<(int pageIndex, Word word)> allWords)
        {
            // Look backwards from the end for natural breaks
            for (int i = chunkWords.Count - 1; i >= 0; i--)
            {
                var text = chunkWords[i].word.Text;

                // Paragraph break (double newline or similar)
                if (text.Contains("\n\n") || text.EndsWith("\n"))
                    return i;

                // Sentence end
                if (text.EndsWith(".") || text.EndsWith("!") || text.EndsWith("?") || text.EndsWith(":"))
                {
                    // Make sure it's not an abbreviation
                    if (text.Length > 2 && char.IsUpper(text[0]) && text.Length < 50)
                        return i;
                }

                // Heading-like (short line, title case)
                if (text.Length < 80 && text.Length > 5)
                {
                    bool isTitleCase = text.Split(' ').All(w => w.Length == 0 || char.IsUpper(w[0]));
                    if (isTitleCase)
                        return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Creates a DocumentChunk from a list of words.
        /// </summary>
        private static DocumentChunk CreateChunk(List<(int pageIndex, Word word)> words, int chunkId)
        {
            if (words.Count == 0) return new DocumentChunk();

            var firstWord = words[0];
            var lastWord = words[^1];

            // Combine text
            var text = string.Join(" ", words.Select(w => w.word.Text));

            // Calculate bounding box
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var (_, word) in words)
            {
                var bb = word.BoundingBox;
                minX = Math.Min(minX, bb.Left);
                minY = Math.Min(minY, bb.Bottom);
                maxX = Math.Max(maxX, bb.Right);
                maxY = Math.Max(maxY, bb.Top);
            }

            // Use the first word's page as the primary page
            int primaryPage = words[0].pageIndex;

            return new DocumentChunk
            {
                ChunkId = $"chunk_{chunkId}",
                PageIndex = primaryPage,
                PageNumber = primaryPage + 1,
                Text = text,
                Left = minX,
                Bottom = minY,
                Right = maxX,
                Top = maxY,
                StartWordIndex = 0,
                EndWordIndex = words.Count - 1
            };
        }
    }
}