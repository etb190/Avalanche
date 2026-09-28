using System;
using System.IO;
using System.Text;
using Avalanche.Services;
using Xunit;

namespace Avalanche.Tests
{
    /// <summary>
    /// The AI chunker cuts chunk text in column-aware reading order, so the passage
    /// locator must find that order on the page even when the content stream
    /// interleaves the columns line by line (the common generator pattern). Before
    /// the reading-order retry existed, AI citations on multi-column pages silently
    /// lost their highlight. LocatePassageInFile tries raw content-stream order
    /// first and only retries in reading order when nothing matched.
    /// </summary>
    public class SearchServiceReadingOrderTests
    {
        const double PageW = 500, PageH = 400;

        // Four lines per column. The content stream draws L0 R0 L1 R1 ... so no
        // left-column-only needle is contiguous in raw content-stream order. Word
        // gaps inside a column (x=50/80 and 280/310, ~13pt) sit below the gutter
        // threshold (max(12, 3.5% of 500) = 17.5) while the column seam (~183pt)
        // recurs on every line, so exactly one gutter is detected.
        static byte[] BuildTwoColumnPdf()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 4; i++)
            {
                double y = 380 - 20 * i;
                sb.Append($"BT /F1 10 Tf 1 0 0 1 50 {y} Tm (L{i}a) Tj ET\n");
                sb.Append($"BT /F1 10 Tf 1 0 0 1 80 {y} Tm (L{i}b) Tj ET\n");
                sb.Append($"BT /F1 10 Tf 1 0 0 1 280 {y} Tm (R{i}a) Tj ET\n");
                sb.Append($"BT /F1 10 Tf 1 0 0 1 310 {y} Tm (R{i}b) Tj ET\n");
            }
            return BuildPdf(sb.ToString(), PageW, PageH);
        }

        static byte[] BuildSingleColumnPdf()
        {
            var sb = new StringBuilder();
            sb.Append("BT /F1 10 Tf 1 0 0 1 50 380 Tm (alpha) Tj ET\n");
            sb.Append("BT /F1 10 Tf 1 0 0 1 90 380 Tm (beta) Tj ET\n");
            sb.Append("BT /F1 10 Tf 1 0 0 1 130 380 Tm (gamma) Tj ET\n");
            sb.Append("BT /F1 10 Tf 1 0 0 1 170 380 Tm (delta) Tj ET\n");
            sb.Append("BT /F1 10 Tf 1 0 0 1 210 380 Tm (epsilon) Tj ET\n");
            return BuildPdf(sb.ToString(), PageW, PageH);
        }

        // Hand-built single-page PDF (same template as PdfTextSizeTests): the
        // content stream keeps its exact Tm operands, so each Tj is one word at
        // a known position in a known order.
        static byte[] BuildPdf(string content, double w, double h)
        {
            string[] objects =
            [
                "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
                "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
                $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] "
                    + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n",
                $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
                "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
                    + "/Encoding /WinAnsiEncoding >>\nendobj\n",
            ];

            var pdf = new StringBuilder("%PDF-1.4\n");
            var offsets = new int[objects.Length];
            for (int i = 0; i < objects.Length; i++)
            {
                offsets[i] = pdf.Length;
                pdf.Append(objects[i]);
            }

            int startXref = pdf.Length;
            pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (int offset in offsets)
                pdf.Append($"{offset:D10} 00000 n \n");
            pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n")
               .Append($"startxref\n{startXref}\n%%EOF\n");

            return Encoding.ASCII.GetBytes(pdf.ToString());
        }

        static string TempPath() => Path.Combine(Path.GetTempPath(), $"ro-{Guid.NewGuid():N}.pdf");

        [Fact]
        public void ColumnOrderSlice_Found_EvenWhenContentStreamInterleaves()
        {
            string path = TempPath();
            try
            {
                File.WriteAllBytes(path, BuildTwoColumnPdf());

                // Chunk text is cut in reading order: L0a L0b L1a L1b ... R0a R0b ...
                var match = SearchService.LocatePassageInFile(path, 0, new[] { "L0a L0b L1a L1b" });

                Assert.NotNull(match);
                Assert.True(match!.MatchedWords >= 4);
                Assert.NotEmpty(match.LineRects);
                Assert.All(match.LineRects, r =>
                {
                    Assert.InRange(r.Left, 0, PageW);
                    Assert.InRange(r.Right, 0, PageW);
                    Assert.InRange(r.Bottom, 0, PageH);
                    Assert.InRange(r.Top, 0, PageH);
                });
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RawContentStreamOrder_StillMatches()
        {
            string path = TempPath();
            try
            {
                File.WriteAllBytes(path, BuildSingleColumnPdf());

                var match = SearchService.LocatePassageInFile(path, 0, new[] { "beta gamma delta" });

                Assert.NotNull(match);
                Assert.True(match!.MatchedWords >= 3);
                Assert.NotEmpty(match.LineRects);
            }
            finally { File.Delete(path); }
        }
    }
}
