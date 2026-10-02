using System;
using System.Collections.Generic;
using Avalanche.Features.Summary;
using Avalanche.Services;
using Xunit;

public sealed class MarkdownNormalizerTests
{
    // Builds synthetic runs: one RunLine per tuple, chars expanded per word
    // ("word" = whitespace-separated token) with the line's point size. Tops
    // descend (PDF space), so prev.Bottom - cur.Top is the visual gap.
    private static PageTextRuns Runs(params (string Text, double Top, double Bottom, double Left, double Size)[] lines)
    {
        var runs = new PageTextRuns { PdfWidth = 612, PdfHeight = 792 };
        int caret = 0;
        int word = 0;
        for (int li = 0; li < lines.Length; li++)
        {
            var (text, top, bottom, left, size) = lines[li];
            var line = new RunLine { Start = caret, Top = top, Bottom = bottom, Left = left, Right = left + text.Length * 6 };
            int lineStart = caret;
            string[] words = text.Split(' ');
            for (int w = 0; w < words.Length; w++)
            {
                foreach (char ch in words[w])
                {
                    runs.Chars.Add(new RunChar(ch.ToString(), left + (caret - lineStart) * 6, left + (caret - lineStart) * 6 + 6, word, li, size));
                    caret++;
                }
                word++;
            }
            line.Count = caret - lineStart;
            runs.Lines.Add(line);
        }
        return runs;
    }

    private static (string Text, double Top, double Bottom, double Left, double Size) L(
        string text, double top, double size = 10, double left = 72, double height = 10) =>
        (text, top, top - height, left, size);

    private static string Build(PageTextRuns runs, List<(int Page, string Norm)>? candidates = null)
    {
        var cands = candidates ?? new List<(int Page, string Norm)>();
        return MarkdownNormalizer.BuildPageMarkdown(runs, 1, cands);
    }

    private static string Normalize(string s)
    {
        // mirror of the private NormalizeHeading: letters lowercased, other
        // runs folded to single spaces
        var sb = new System.Text.StringBuilder();
        bool space = true;
        foreach (char ch in s)
        {
            if (char.IsLetter(ch)) { sb.Append(char.ToLowerInvariant(ch)); space = false; }
            else if (!space) { sb.Append(' '); space = true; }
        }
        return sb.ToString().Trim();
    }

    [Fact]
    public void HeadingTiers_Map_SizeRatio_To_Marks()
    {
        // three headings separated by body lines (consecutive heading-sized
        // lines would merge into one wrapped title), median body size = 10.
        var runs = Runs(
            L("The Big Title", 700, 16),          // 1.6x -> '#'
            L("Body text one here.", 660),
            L("Chapter Section", 620, 13),        // 1.3x -> '##'
            L("Body text two here.", 580),
            L("Small Subsection", 540, 12),       // 1.2x -> '###'
            L("Body text three follows.", 500));
        string md = Build(runs);
        Assert.Contains("# The Big Title", md);
        Assert.Contains("## Chapter Section", md);
        Assert.Contains("### Small Subsection", md);
        Assert.DoesNotContain("[[H]]", md);
    }

    [Fact]
    public void DeHyphenation_Merges_Split_Word()
    {
        var runs = Runs(
            L("The devel-", 700),
            L("opment of cities", 685));
        string md = Build(runs);
        Assert.Contains("development of cities", md);
        Assert.DoesNotContain("devel-", md);
    }

    [Fact]
    public void Regular_Leading_Reflows_With_Single_Space()
    {
        var runs = Runs(
            L("The first line ends here", 700),
            L("and the second continues it.", 685));
        string md = Build(runs);
        Assert.Contains("The first line ends here and the second continues it.", md);
    }

    [Fact]
    public void Sentence_End_Plus_Large_Gap_Breaks_Paragraph()
    {
        // gaps 5, 5, 25: the median (normal leading) is 5, so the 25-point gap
        // after a sentence end clears 1.3x and opens a new paragraph.
        var runs = Runs(
            L("The second thought begins.", 700),
            L("More prose in the same paragraph.", 685),
            L("The first thought ends here.", 670),
            L("A new paragraph starts.", 635));
        string md = Build(runs);
        Assert.Contains("ends here.\n\nA new paragraph starts.", md);
    }

    [Fact]
    public void Large_Gap_Without_Sentence_End_Reflows()
    {
        // spec: a line that does not end with sentence punctuation joins the
        // next one regardless of the gap - the clause continues.
        var runs = Runs(
            L("The first clause carries on", 700),
            L("through this middle line and", 685),
            L("reaches the comma after silver,", 670),
            L("gold, and bronze artifacts.", 635));
        string md = Build(runs);
        Assert.Contains("silver, gold, and bronze artifacts.", md);
        Assert.DoesNotContain("\n\n", md);
    }

    [Fact]
    public void Indented_Line_Opens_New_Paragraph()
    {
        // lefts 72, 92, 72: the page's median left margin is 72, the indented
        // line clears it and the previous line's left.
        var runs = Runs(
            L("The paragraph before the quote.", 700),
            L("An indented quotation starts.", 685, left: 92),
            L("Prose at the normal margin again.", 670));
        string md = Build(runs);
        Assert.Contains("before the quote.\n\nAn indented quotation", md);
    }

    [Fact]
    public void Bullets_Become_List_Items_And_Continuation_Joins()
    {
        var runs = Runs(
            L("Intro sentence before the list.", 700),
            L("\u2022 First item of the list", 685),
            L("which wraps onto a second line", 670),
            L("\u2022 Second item", 655));
        string md = Build(runs);
        Assert.Contains("- First item of the list which wraps onto a second line", md);
        Assert.Contains("\n- Second item", md);
        Assert.DoesNotContain("\u2022", md);
    }

    [Fact]
    public void Math_Asterisk_Is_Not_A_Bullet()
    {
        var runs = Runs(
            L("The factor scales as", 700),
            L("*0.5 per generation", 685));
        string md = Build(runs);
        Assert.DoesNotContain("- 0.5", md);
        Assert.Contains("*0.5 per generation", md);
    }

    [Fact]
    public void En_Dash_Bullet_Converts()
    {
        var runs = Runs(
            L("Supplies needed:", 700),
            L("\u2013 rope and canvas", 685));
        string md = Build(runs);
        Assert.Contains("- rope and canvas", md);
    }

    [Fact]
    public void Running_Heads_Are_Filtered_Across_Pages()
    {
        var cands = new List<(int Page, string Norm)>();
        var pages = new List<string>();
        for (int p = 1; p <= 6; p++)
        {
            var runs = Runs(
                L("Ancient Mesopotamia", 700, 12),   // repeats on every page
                L($"Page {p} body prose follows the running head.", 685));
            pages.Add(MarkdownNormalizer.BuildPageMarkdown(runs, p, cands));
        }

        var heads = MarkdownNormalizer.DetectRunningHeads(cands, 6);
        Assert.Contains(Normalize("Ancient Mesopotamia"), heads);

        string stripped = MarkdownNormalizer.StripRunningHeads(pages[0], heads);
        Assert.DoesNotContain("Ancient Mesopotamia", stripped);
        Assert.Contains("body prose", stripped);
    }

    [Fact]
    public void No_Headings_When_All_Text_Shares_One_Size()
    {
        var runs = Runs(
            L("Everything is the same size here.", 700),
            L("No heading can be detected.", 685));
        string md = Build(runs);
        Assert.DoesNotContain("#", md);
        Assert.Contains("Everything is the same size here.", md);
    }

    [Fact]
    public void DetectRunningHeads_Keeps_Single_Strays()
    {
        var cands = new List<(int Page, string Norm)>
        {
            (1, Normalize("One Real Chapter")),
            (2, Normalize("One Real Chapter")),
        };
        var heads = MarkdownNormalizer.DetectRunningHeads(cands, 10);
        Assert.Empty(heads);   // 2 of 10 pages: below the furniture threshold
    }
}
