using Avalanche.Features.Summary;
using Xunit;

namespace Avalanche.Tests;

// The Explain source-selection helpers (v1.18.1): the digest's typography is
// folded into the extraction's plain characters, and a range too large for one
// request ships the pages the highlighted words actually live on instead of a
// provider-side truncation that keeps only page one.

public sealed class SummaryExplainSourceTests
{
    [Fact]
    public void NormalizeExcerpt_FoldsDigestTypography()
    {
        string folded = ExplainSourceSelection.NormalizeExcerpt(
            "ice\u2011free corridor after 12\u00A0700 BC\u2019s edge \u201Ccold reversal\u201D");
        Assert.Contains("ice-free", folded);
        Assert.Contains("12 700", folded);
        Assert.Contains("BC's", folded);
        Assert.Contains("\"cold reversal\"", folded);
    }

    [Fact]
    public void NormalizeExcerpt_CollapsesWhitespaceAndTrims()
    {
        Assert.Equal("a b c", ExplainSourceSelection.NormalizeExcerpt("  a\n \t b   c \u2009"));
        Assert.Equal(string.Empty, ExplainSourceSelection.NormalizeExcerpt(string.Empty));
        Assert.Equal(string.Empty, ExplainSourceSelection.NormalizeExcerpt(null));
    }

    [Fact]
    public void SelectExcerptPages_SmallRange_ReturnsUnabridged()
    {
        string raw = "[p. 1]\nfirst page text\n\n[p. 2]\nsecond page text";
        string picked = ExplainSourceSelection.SelectExcerptPages(raw, "first page", 32000, out bool trimmed);
        Assert.False(trimmed);
        Assert.Equal(raw, picked);
    }

    [Fact]
    public void SelectExcerptPages_LargeRange_KeepsTheExcerptPages()
    {
        string filler = string.Concat(Enumerable.Repeat("stone ridge field ", 400));
        var sb = new System.Text.StringBuilder();
        for (int page = 1; page <= 40; page++)
        {
            string body = $"[p. {page}]\n{filler}";
            if (page == 30)
            {
                body = $"[p. {page}]\nglacial megafauna collapse after the thaw {filler}";
            }

            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            sb.Append(body);
        }

        string raw = sb.ToString();
        string picked = ExplainSourceSelection.SelectExcerptPages(raw, "glacial megafauna collapse", 8000, out bool trimmed);
        Assert.True(trimmed);
        Assert.Contains("[p. 30]", picked);
        Assert.Contains("glacial megafauna collapse", picked);
        Assert.True(picked.Length <= 32000 + filler.Length + 100,
            "the top page is taken even past the budget, but nothing else");
        Assert.DoesNotContain("[p. 39]", picked);
    }

    [Fact]
    public void SelectExcerptPages_PreservesPageOrder()
    {
        // ~2.2k chars per page: with the 9k budget the two scoring pages (5, 22)
        // both fit alongside one filler page, so the assertion really exercises
        // the book-order restoration of multiple chosen pages.
        string filler = string.Concat(Enumerable.Repeat("stone ridge field ", 120));
        var sb = new System.Text.StringBuilder();
        for (int page = 1; page <= 30; page++)
        {
            string body = $"[p. {page}]\n{filler}";
            if (page == 5 || page == 22)
            {
                body = $"[p. {page}]\ncold reversal evidence {filler}";
            }

            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            sb.Append(body);
        }

        string picked = ExplainSourceSelection.SelectExcerptPages(sb.ToString(), "cold reversal evidence", 9000, out bool trimmed);
        Assert.True(trimmed);
        int first = picked.IndexOf("[p. 5]", StringComparison.Ordinal);
        int second = picked.IndexOf("[p. 22]", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, "chosen pages ride in book order");
        Assert.DoesNotContain("[p. 30]", picked);
    }

    [Fact]
    public void SelectExcerptPages_NoOverlap_FallsBackToFirstPages()
    {
        string filler = string.Concat(Enumerable.Repeat("stone ridge field ", 400));
        var sb = new System.Text.StringBuilder();
        for (int page = 1; page <= 20; page++)
        {
            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            sb.Append($"[p. {page}]\n{filler}");
        }

        string picked = ExplainSourceSelection.SelectExcerptPages(sb.ToString(), "entirely absent words", 6000, out bool trimmed);
        Assert.True(trimmed);
        Assert.StartsWith("[p. 1]", picked, StringComparison.Ordinal);
    }

    [Fact]
    public void IsWordChar_MatchesThePopupBoundaries()
    {
        Assert.True(DigestWordHighlightAdorner.IsWordChar('a'));
        Assert.True(DigestWordHighlightAdorner.IsWordChar('9'));
        Assert.False(DigestWordHighlightAdorner.IsWordChar(' '));
        Assert.False(DigestWordHighlightAdorner.IsWordChar('-'));
        Assert.False(DigestWordHighlightAdorner.IsWordChar('.'));
    }
}
