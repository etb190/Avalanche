using System.Linq;
using Avalanche.Features.AI;
using Xunit;

namespace Avalanche.Tests;

public sealed class DocumentChunkerTests
{
    private static IndexedWord W(string text, double left, double top, double width = 30, double height = 10)
        => new IndexedWord(text, left, top - height, left + width, top);

    private static PageWordStream Stream(params (string[] words, double top)[] lines)
    {
        // Single-column page: one line per entry, 660pt wide.
        var raw = new List<IndexedWord>();
        double x = 50;
        foreach (var (words, top) in lines)
        {
            foreach (var w in words)
            {
                raw.Add(W(w, x, top, 6 * (w.Length + 1)));
                x += 6 * (w.Length + 1) + 4;
            }
            x = 50;
        }
        return PageWordStreamBuilder.Build(raw, 660, 800);
    }

    [Fact]
    public void ReadingOrderIsTopToBottomWithinOneColumn()
    {
        var stream = Stream(
            (new[] { "Alpha", "beta" }, 700),
            (new[] { "Gamma", "delta" }, 680));
        Assert.Equal(new[] { "Alpha", "beta", "Gamma", "delta" }, stream.Words.Select(w => w.Text).ToArray());
    }

    [Fact]
    public void TwoColumnPageIsReadColumnByColumn()
    {
        // Left column: four lines of body text; right column: four more.
        // A wide, recurring gutter at x=360 must be detected as a column seam.
        var raw = new List<IndexedWord>();
        for (int line = 0; line < 6; line++)
        {
            double top = 700 - line * 14;
            raw.Add(W("L" + line, 50, top));
            raw.Add(W("words", 84, top));
            raw.Add(W("here", 130, top));
            raw.Add(W("R" + line, 380, top));
            raw.Add(W("more", 414, top));
            raw.Add(W("words", 448, top));
        }
        var stream = PageWordStreamBuilder.Build(raw, 660, 800);

        var texts = stream.Words.Select(w => w.Text).ToList();
        // All left-column words come before any right-column word.
        int firstRight = texts.IndexOf("R0");
        int lastLeft = texts.LastIndexOf("L5");
        Assert.True(lastLeft < firstRight, $"reading order crossed the gutter: {string.Join(',', texts)}");
        // Within each column, lines run top to bottom.
        Assert.True(texts.IndexOf("L0") < texts.IndexOf("L5"));
        Assert.True(texts.IndexOf("R0") < texts.IndexOf("R5"));
    }

    [Fact]
    public void PageEndDoesNotForceAChunk()
    {
        var chunker = new DocumentChunker(new ChunkerOptions
        {
            TargetChunkSize = 10000,   // never reached
            MaxChunkSize = 20000,
            MinChunkWords = 4,
            OverlapWords = 0
        });
        chunker.AppendPage(0, Stream((new[] { "One", "two", "three", "four", "five." }, 700)));
        chunker.AppendPage(1, Stream((new[] { "Six", "seven", "eight", "nine", "ten." }, 700)));

        var chunks = chunker.Finish();
        var chunk = Assert.Single(chunks);
        Assert.Equal(new[] { 0, 1 }, chunk.PageIndices);
    }

    [Fact]
    public void SentenceBreakIsPreferredWhenTargetReached()
    {
        var chunker = new DocumentChunker(new ChunkerOptions
        {
            TargetChunkSize = 40,
            MaxChunkSize = 120,
            MinChunkWords = 3,
            OverlapWords = 0
        });
        chunker.AppendPage(0, Stream(
            (new[] { "The", "fee", "doubles.", "Patients", "may", "opt", "out.", "More", "text", "follows." }, 700)));

        var chunks = chunker.Finish();
        Assert.True(chunks.Count >= 2, "expected sentence-aligned chunks");
        Assert.EndsWith("out.", chunks[0].Text);
    }

    [Fact]
    public void HeadingStartsANewSection()
    {
        var raw = new List<IndexedWord>();
        // Body line (normal height), then a HEADING in larger type, then body.
        raw.Add(W("Some", 50, 700));
        raw.Add(W("body", 84, 700));
        raw.Add(W("text", 116, 700));
        raw.Add(W("Introduction", 50, 660, 6 * 12, 16));   // heading: 16pt
        raw.Add(W("Here", 50, 630));
        raw.Add(W("is", 84, 630));
        raw.Add(W("more", 104, 630));

        var stream = PageWordStreamBuilder.Build(raw, 660, 800);
        Assert.NotNull(stream.HeadingRange);

        var chunker = new DocumentChunker(new ChunkerOptions
        {
            TargetChunkSize = 40,
            MaxChunkSize = 200,
            MinChunkWords = 3,
            OverlapWords = 0
        });
        chunker.AppendPage(0, stream);
        var chunks = chunker.Finish();

        var withHeading = chunks.Where(c => c.SectionHeading != null).ToList();
        Assert.NotEmpty(withHeading);
        Assert.Equal("Introduction", withHeading[0].SectionHeading);
        // The heading word opens the chunk it names.
        Assert.StartsWith("Introduction", chunks.First(c => c.SectionHeading == "Introduction").Text);
    }

    [Fact]
    public void ConsecutiveChunksShareAnOverlapTail()
    {
        var chunker = new DocumentChunker(new ChunkerOptions
        {
            TargetChunkSize = 50,
            MaxChunkSize = 90,
            MinChunkWords = 3,
            OverlapWords = 3
        });
        chunker.AppendPage(0, Stream(
            (new[] { "alpha", "beta.", "gamma", "delta.", "epsilon", "zeta.", "eta", "theta." }, 700)));

        var chunks = chunker.Finish();
        Assert.True(chunks.Count >= 2);
        var firstTail = chunks[0].Text.Split(' ')[^1];
        Assert.Contains(firstTail, chunks[1].Text);
    }

    [Fact]
    public void TinyRemainderMergesIntoPreviousChunk()
    {
        var chunker = new DocumentChunker(new ChunkerOptions
        {
            TargetChunkSize = 30,
            MaxChunkSize = 200,
            MinChunkWords = 6,
            OverlapWords = 0
        });
        chunker.AppendPage(0, Stream(
            (new[] { "alpha", "beta.", "gamma", "delta.", "epsilon", "zeta.", "eta" }, 700)));

        var chunks = chunker.Finish();
        Assert.True(chunks[^1].Text.Split(' ').Length >= 6, "trailing fragment was not merged");
    }

    [Fact]
    public void ChunkRecordsPerPageGeometryRanges()
    {
        var chunker = new DocumentChunker(new ChunkerOptions
        {
            TargetChunkSize = 10000,
            MaxChunkSize = 20000,
            MinChunkWords = 4,
            OverlapWords = 0
        });
        chunker.AppendPage(0, Stream((new[] { "One", "two", "three." }, 700)));
        chunker.AppendPage(1, Stream((new[] { "Four", "five", "six." }, 700)));

        var chunk = Assert.Single(chunker.Finish());
        Assert.Equal(new[] { 0, 1 }, chunk.PageIndices);
        Assert.Equal(2, chunk.WordRanges.Count);
        Assert.Equal(new[] { 0, 2 }, chunk.WordRanges[0]);
        Assert.Equal(new[] { 3, 5 }, chunk.WordRanges[1]);
        Assert.All(chunk.PdfCoordinates, c => Assert.Equal(4, c.Length));
    }
}
