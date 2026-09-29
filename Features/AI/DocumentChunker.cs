using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Avalanche.Features.AI
{
    /// <summary>A word in reading order with its PDF-space box
    /// (left, bottom, right, top - PDF's bottom-left origin).</summary>
    public readonly record struct IndexedWord(string Text, double Left, double Bottom, double Right, double Top)
    {
        public double Height => Top - Bottom;
    }

    /// <summary>
    /// One page's words in column-aware reading order plus the line metadata
    /// the chunker needs. PdfPig yields words in content-stream order, which
    /// scrambles multi-column pages and never contains newlines - everything
    /// downstream (paragraph rules, headings) is derived here from geometry.
    /// </summary>
    public sealed class PageWordStream
    {
        public List<IndexedWord> Words { get; } = new();
        /// <summary>Word indices (into Words) where a new visual line begins.</summary>
        public HashSet<int> LineStarts { get; } = new();
        /// <summary>Word indices that begin a new paragraph (gap, indent or short
        /// previous line). Candidates for chunk breaks.</summary>
        public HashSet<int> ParagraphStarts { get; } = new();
        /// <summary>[start, end) word range of a heading line, when one was found.</summary>
        public int[]? HeadingRange { get; set; }
        public string? HeadingText { get; set; }
    }

    /// <summary>
    /// Derives reading order and paragraph/heading structure for one page from
    /// raw word boxes. Words are banded into visual lines (>= 50% vertical
    /// overlap), consistent column gutters are detected across the page, and
    /// text is emitted column by column, top to bottom - the same layout model
    /// the viewer's own text selection uses.
    /// </summary>
    public static class PageWordStreamBuilder
    {
        private sealed class Band
        {
            public double Top, Bottom;
            public List<IndexedWord> Words = new();
        }

        public static PageWordStream Build(IReadOnlyList<IndexedWord> rawWords, double pageWidth, double pageHeight)
        {
            var stream = new PageWordStream();
            if (rawWords.Count == 0) return stream;

            // 1) Band words into visual lines (insertion order, like text selection).
            var bands = new List<Band>();
            foreach (var w in rawWords)
            {
                double t = w.Top, b = w.Bottom;
                int found = -1;
                for (int i = 0; i < bands.Count; i++)
                {
                    double overlap = Math.Min(bands[i].Top, t) - Math.Max(bands[i].Bottom, b);
                    double minH = Math.Min(bands[i].Top - bands[i].Bottom, t - b);
                    if (minH > 0 && overlap >= minH * 0.5) { found = i; break; }
                }
                if (found < 0)
                    bands.Add(new Band { Top = t, Bottom = b, Words = { w } });
                else
                {
                    var band = bands[found];
                    band.Words.Add(w);
                    band.Top = Math.Max(band.Top, t);
                    band.Bottom = Math.Min(band.Bottom, b);
                }
            }

            // 2) Lines top to bottom; words within a line left to right.
            foreach (var band in bands) band.Words.Sort((a, b) => a.Left.CompareTo(b.Left));
            bands.Sort((a, b) => b.Top.CompareTo(a.Top));

            // 3) Column gutters: wide horizontal gaps recurring across many lines.
            var gutters = DetectGutters(bands, pageWidth);

            var heights = rawWords.Select(w => w.Height).Where(h => h > 0).OrderBy(h => h).ToList();
            double medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 10.0;

            double minLeft = bands.Min(b => b.Words[0].Left);
            double maxRight = bands.Max(b => b.Words[b.Words.Count - 1].Right);
            double medianBandHeight = Median(bands.Select(b => b.Top - b.Bottom).Where(h => h > 0));

            // 4) Reading order: per column (left to right), lines top to bottom.
            for (int col = 0; col <= gutters.Count; col++)
            {
                double gLeft = col == 0 ? double.NegativeInfinity : gutters[col - 1];
                double gRight = col == gutters.Count ? double.PositiveInfinity : gutters[col];

                var colBands = new List<Band>();
                foreach (var band in bands)
                {
                    // A band may span columns when a gutter interrupts only some
                    // lines (tables, headings); split the band's words per column.
                    var words = band.Words.Where(w => Center(w) > gLeft && Center(w) <= gRight).ToList();
                    if (words.Count > 0) colBands.Add(new Band { Top = band.Top, Bottom = band.Bottom, Words = words });
                }

                bool firstOfColumn = true;
                Band? prevBand = null;
                foreach (var band in colBands)
                {
                    int start = stream.Words.Count;
                    int wordCount = band.Words.Count;
                    int charCount = band.Words.Sum(w => w.Text.Length + 1);

                    bool paraStart;
                    if (firstOfColumn)
                    {
                        // A new column opens a new block, but the page's very
                        // first line may continue a paragraph from the page
                        // before, so it is not forced.
                        paraStart = col > 0;
                        firstOfColumn = false;
                    }
                    else if (prevBand != null)
                    {
                        double gap = prevBand.Bottom - band.Top;
                        double indent = band.Words[0].Left - minLeft;
                        bool shortPrev = prevBand.Words[prevBand.Words.Count - 1].Right < maxRight - 0.12 * pageWidth;
                        paraStart = gap > 1.6 * medianBandHeight
                                    || indent > 2.0 * medianHeight
                                    || shortPrev;
                    }
                    else
                    {
                        paraStart = false;
                    }

                    stream.Words.AddRange(band.Words);
                    stream.LineStarts.Add(start);
                    if (paraStart) stream.ParagraphStarts.Add(start);

                    // Heading: short line in larger type. Whatever follows it
                    // starts a new paragraph.
                    if (wordCount <= 8 && charCount <= 60 && medianHeight > 0 && stream.HeadingRange is null)
                    {
                        var hs = band.Words.Select(w => w.Height).Where(h => h > 0).OrderBy(h => h).ToList();
                        double lineH = hs.Count > 0 ? hs[hs.Count / 2] : 0;
                        if (lineH >= 1.2 * medianHeight)
                        {
                            stream.HeadingRange = new[] { start, start + wordCount };
                            stream.HeadingText = string.Join(" ", band.Words.Select(w => w.Text.Trim()));
                            stream.ParagraphStarts.Add(start + wordCount);
                        }
                    }

                    prevBand = band;
                }
            }

            return stream;
        }

        private static double Center(IndexedWord w) => (w.Left + w.Right) / 2.0;

        private static double Median(IEnumerable<double> values)
        {
            var list = values.OrderBy(v => v).ToList();
            return list.Count == 0 ? 0 : list[list.Count / 2];
        }

        /// <summary>Finds vertical gutters: wide gaps whose X position recurs
        /// across several lines (a real column seam, not a table or centered
        /// title). Returns gutter X positions in ascending order.</summary>
        private static List<double> DetectGutters(List<Band> bands, double pageWidth)
        {
            double minGap = Math.Max(12, 0.035 * pageWidth);

            var candidates = new List<(int band, double mid, double left, double right)>();
            for (int bi = 0; bi < bands.Count; bi++)
            {
                var words = bands[bi].Words;
                for (int i = 1; i < words.Count; i++)
                {
                    double gap = words[i].Left - words[i - 1].Right;
                    if (gap >= minGap)
                        candidates.Add((bi, (words[i - 1].Right + words[i].Left) / 2.0, words[i - 1].Right, words[i].Left));
                }
            }
            if (candidates.Count == 0) return new List<double>();

            // Cluster candidate midpoints within 1.5% of the page width.
            double tol = Math.Max(6, 0.015 * pageWidth);
            var ordered = candidates.OrderBy(c => c.mid).ToList();

            var result = new List<double>();
            var cluster = new List<(int band, double mid, double left, double right)>();
            double? clusterMid = null;

            void FlushCluster()
            {
                if (cluster.Count == 0) return;
                // A gutter must split at least three lines and a meaningful
                // share of the page; isolated wide gaps (tables, covers,
                // centered titles) never qualify.
                var distinctBands = cluster.Select(c => c.band).Distinct().ToList();
                if (distinctBands.Count >= 3 && distinctBands.Count >= 0.3 * bands.Count)
                    result.Add(cluster.Average(c => (c.left + c.right) / 2.0));
                cluster.Clear();
            }

            foreach (var c in ordered)
            {
                if (clusterMid.HasValue && Math.Abs(c.mid - clusterMid.Value) > tol)
                {
                    FlushCluster();
                    clusterMid = null;
                }
                if (!clusterMid.HasValue) clusterMid = c.mid;
                cluster.Add(c);
            }
            FlushCluster();

            return result.OrderBy(x => x).ToList();
        }
    }

    /// <summary>Configuration for DocumentChunker (mirrors IndexingOptions).</summary>
    public sealed class ChunkerOptions
    {
        public int TargetChunkSize { get; set; } = 500;
        public int MaxChunkSize { get; set; } = 800;
        public int MinChunkWords { get; set; } = 20;
        /// <summary>Words carried from the end of one chunk onto the next, so
        /// answers spanning a chunk boundary stay retrievable.</summary>
        public int OverlapWords { get; set; } = 40;
    }

    /// <summary>An assembled chunk, independent of persistence so the chunker
    /// stays unit-testable. DocumentIndexer maps these onto DocumentChunk.</summary>
    public sealed class AssembledChunk
    {
        public int ChunkIndex { get; set; }
        public string Text { get; set; } = "";
        public string? SectionHeading { get; set; }
        public long CharOffset { get; set; }
        public List<int> PageIndices { get; } = new();
        /// <summary>Chunk-relative [startWord, endWord] per page (word indices
        /// within this chunk's whitespace-split Text).</summary>
        public List<int[]> WordRanges { get; } = new();
        /// <summary>Per-page [left, bottom, right, top] union of the chunk's words.</summary>
        public List<float[]> PdfCoordinates { get; } = new();
    }

    /// <summary>
    /// Streams pages (in reading order) into chunks. Breaks prefer real
    /// structure - section headings, paragraph boundaries, sentence ends -
    /// never "any capitalized word", and page ends no longer force a chunk.
    /// A bounded word overlap bridges consecutive chunks.
    /// </summary>
    public sealed class DocumentChunker
    {
        private sealed class PendingWord
        {
            public IndexedWord W;
            public int Page;
            public bool ParaStart;
            public bool HeadingStart;
        }

        private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
        {
            "mr","mrs","ms","dr","prof","sr","jr","st","vs","fig","eq","no","al",
            "eg","ie","etc","approx","dept","est","inc","ltd","co","corp","ed",
            "eds","vol","pp","cf","ca","circa"
        };

        private readonly ChunkerOptions _options;
        private readonly List<PendingWord> _pending = new();
        private readonly List<AssembledChunk> _chunks = new();
        private List<PendingWord> _lastChunkWords = new();
        private long _charOffset;
        private int _pendingChars;

        // The section heading waiting to be claimed by the chunk that actually
        // contains the first word after the heading line (word-anchored, so
        // overlapping breaks can never mis-assign it to an earlier chunk).
        private string? _pendingHeading;
        private PendingWord? _headingAnchor;

        public DocumentChunker(ChunkerOptions? options = null)
        {
            _options = options ?? new ChunkerOptions();
        }

        public int ChunkCount => _chunks.Count;

        /// <summary>Appends one page's words (already in reading order). Words
        /// flow across page boundaries; a page end never forces a chunk.</summary>
        public void AppendPage(int pageIndex, PageWordStream stream)
        {
            if (stream.Words.Count == 0) return;

            // A heading opens a new section: finish the current chunk first when
            // it is large enough to stand alone, and give the words that follow
            // the heading their section context.
            if (stream.HeadingRange is { } hr && hr.Length == 2 && stream.HeadingText is { } heading)
            {
                if (_pending.Count >= _options.MinChunkWords)
                    FlushChunk();
                if (_pendingHeading is null)
                    _pendingHeading = heading;
            }

            for (int i = 0; i < stream.Words.Count; i++)
            {
                bool isHeadingStart = stream.HeadingRange is { } r && r.Length == 2 && i == r[0];
                var pendingWord = new PendingWord
                {
                    W = stream.Words[i],
                    Page = pageIndex,
                    ParaStart = stream.ParagraphStarts.Contains(i),
                    HeadingStart = isHeadingStart
                };
                _pending.Add(pendingWord);
                _pendingChars += stream.Words[i].Text.Length + 1;

                // The first word after the heading line anchors its section.
                if (stream.HeadingRange is { } hrA && hrA.Length == 2 && i == hrA[1])
                    _headingAnchor = pendingWord;

                if (_pendingChars >= _options.TargetChunkSize)
                    TryBreak();
            }
        }

        /// <summary>Finishes the last chunk. A trailing remainder smaller than
        /// MinChunkWords merges into the previous chunk instead of shipping a
        /// five-word fragment.</summary>
        public List<AssembledChunk> Finish()
        {
            if (_pending.Count > 0)
            {
                if (_pending.Count < _options.MinChunkWords && _chunks.Count > 0
                    && _lastChunkWords.Count + _pending.Count <= _options.MaxChunkSize)
                {
                    var last = _chunks[_chunks.Count - 1];
                    var merged = new List<PendingWord>(_lastChunkWords);
                    merged.AddRange(_pending);
                    var rebuilt = BuildChunk(merged, last.ChunkIndex, last.SectionHeading);
                    _lastChunkWords = merged;
                    _chunks[_chunks.Count - 1] = rebuilt;
                    _pending.Clear();
                    _pendingChars = 0;
                }
                else
                {
                    FlushChunk();
                }
            }
            return _chunks;
        }

        private void TryBreak()
        {
            int breakIdx = FindBreakIndex();
            if (breakIdx < 0)
            {
                if (_pendingChars < _options.MaxChunkSize) return;
                // Hard overflow: cut at the first word boundary that keeps the
                // head chunk above MinChunkWords. Long tokens - URLs, file
                // paths, base64, CJK runs without spaces, merged glyph runs
                // from overlapping text - can push _pendingChars past
                // MaxChunkSize while FEWER than MinChunkWords words are
                // pending. Clamp the cut to the pending count so the split
                // stays in bounds instead of throwing and killing the whole
                // index build, which left the AI chat dead for such files.
                breakIdx = Math.Min(_options.MinChunkWords, _pending.Count);
                if (breakIdx <= 0) return;
            }

            var head = _pending.GetRange(0, breakIdx);
            var tail = _pending.GetRange(breakIdx, _pending.Count - breakIdx);

            BuildAndAdd(head);

            // Carry a bounded overlap of the chunk's tail onto the next one.
            int overlap = Math.Min(_options.OverlapWords, (int)(head.Count * 0.25));
            _pending.Clear();
            if (overlap > 0)
            {
                foreach (var w in head.GetRange(head.Count - overlap, overlap))
                    _pending.Add(new PendingWord { W = w.W, Page = w.Page, ParaStart = false, HeadingStart = false });
            }
            _pending.AddRange(tail);
            RecomputePendingChars();
        }

        /// <summary>Scans backwards for the best break, strongest boundary
        /// first: section heading, paragraph start, sentence end, clause end.
        /// Never breaks inside the first MinChunkWords words.</summary>
        private int FindBreakIndex()
        {
            int para = -1, sentence = -1, clause = -1;
            for (int idx = _pending.Count - 1; idx >= _options.MinChunkWords; idx--)
            {
                if (_pending[idx].HeadingStart) return idx;
                if (para < 0 && _pending[idx].ParaStart) para = idx;
                if (idx == 0) break;
                var prev = _pending[idx - 1].W.Text;
                if (sentence < 0 && EndsSentence(prev)) sentence = idx;
                if (clause < 0 && EndsClause(prev)) clause = idx;
            }
            if (para >= 0) return para;
            if (sentence >= 0) return sentence;
            return clause;
        }

        private void FlushChunk()
        {
            if (_pending.Count == 0) return;
            BuildAndAdd(_pending);
            _pending.Clear();
            _pendingChars = 0;
        }

        private void BuildAndAdd(List<PendingWord> words)
        {
            if (words.Count == 0) return;

            // The heading only names the chunk that actually contains the word
            // right after the heading line.
            string? heading = null;
            if (_pendingHeading is not null && _headingAnchor is not null && words.Contains(_headingAnchor))
            {
                heading = _pendingHeading;
                _pendingHeading = null;
                _headingAnchor = null;
            }

            var chunk = BuildChunk(words, _chunks.Count, heading);
            _lastChunkWords = words;
            _chunks.Add(chunk);
        }

        private AssembledChunk BuildChunk(List<PendingWord> words, int chunkIndex, string? heading)
        {
            var text = string.Join(" ", words.Select(w => w.W.Text));
            var chunk = new AssembledChunk
            {
                ChunkIndex = chunkIndex,
                Text = text,
                SectionHeading = heading,
                CharOffset = _charOffset
            };
            _charOffset += text.Length + 1;

            int wordOffset = 0;
            foreach (var group in words.GroupBy(w => w.Page).OrderBy(g => g.Key))
            {
                int start = wordOffset;
                int end = wordOffset + group.Count() - 1;
                chunk.PageIndices.Add(group.Key);
                chunk.WordRanges.Add(new[] { start, end });

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var pw in group)
                {
                    var bb = pw.W;
                    minX = Math.Min(minX, bb.Left);
                    minY = Math.Min(minY, bb.Bottom);
                    maxX = Math.Max(maxX, bb.Right);
                    maxY = Math.Max(maxY, bb.Top);
                }
                chunk.PdfCoordinates.Add(new[]
                {
                    (float)minX, (float)minY, (float)maxX, (float)maxY
                });

                wordOffset += group.Count();
            }
            return chunk;
        }

        private void RecomputePendingChars()
        {
            _pendingChars = 0;
            foreach (var w in _pending) _pendingChars += w.W.Text.Length + 1;
        }

        private static bool EndsSentence(string text)
        {
            if (text.Length < 2) return false;
            char last = text[^1];
            if (last == '!' || last == '?') return true;
            if (last != '.') return false;

            var stem = text.TrimEnd('.').Replace(".", "").ToLowerInvariant();
            if (stem.Length == 0) return false;             // "..." ellipsis
            if (Abbreviations.Contains(stem)) return false; // "Mr.", "e.g.", "vs."
            if (stem.Length == 1) return false;             // initials "J."
            if (stem.All(char.IsDigit) && stem.Length <= 3) return false; // list numbers "1."
            return true;
        }

        private static bool EndsClause(string text)
        {
            if (text.Length == 0) return false;
            char last = text[^1];
            return last is ',' or ':' or ';';
        }
    }
}