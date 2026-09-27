using Avalanche.Services;
using Xunit;

namespace Avalanche.Tests;

public sealed class TextRunServiceTests
{
    [Theory]
    [InlineData("English text", false)]
    [InlineData("متن فارسی", true)]
    [InlineData("نص عربي", true)]
    [InlineData("טקסט עברי", true)]
    [InlineData("1234", false)]
    public void DetectsLineDirection(string text, bool expected)
        => Assert.Equal(expected, TextRunService.IsRightToLeftText([text]));

    [Fact]
    public void RightToLeftCaretMovesFromRightEdgeToLeftEdge()
    {
        var runs = new PageTextRuns();
        runs.Chars.Add(new RunChar("א", 90, 100, 0, 0));
        runs.Chars.Add(new RunChar("ב", 80, 90, 0, 0));
        runs.Chars.Add(new RunChar("ג", 70, 80, 0, 0));
        runs.Lines.Add(new RunLine
        {
            Start = 0,
            Count = 3,
            Top = 20,
            Bottom = 10,
            Left = 70,
            Right = 100,
            RightToLeft = true,
        });

        Assert.Equal(0, TextRunService.CaretFromPoint(runs, 101, 15));
        Assert.Equal(1, TextRunService.CaretFromPoint(runs, 89, 15));
        Assert.Equal(3, TextRunService.CaretFromPoint(runs, 69, 15));
    }

    [Fact]
    public void CaretUsesHorizontalPositionWhenColumnsShareTheSameLineHeight()
    {
        var runs = new PageTextRuns();
        runs.Chars.Add(new RunChar("L", 10, 20, 0, 0));
        runs.Chars.Add(new RunChar("R", 110, 120, 1, 1));
        runs.Lines.Add(new RunLine
        {
            Start = 0, Count = 1, Top = 20, Bottom = 10,
            Left = 10, Right = 20,
        });
        runs.Lines.Add(new RunLine
        {
            Start = 1, Count = 1, Top = 20, Bottom = 10,
            Left = 110, Right = 120,
        });

        Assert.Equal(0, TextRunService.CaretFromPoint(runs, 11, 15));
        Assert.Equal(1, TextRunService.CaretFromPoint(runs, 111, 15));
        Assert.Equal(2, TextRunService.CaretFromPoint(runs, 121, 15));
    }
}
