using Avalanche.Features.AI;
using Xunit;

namespace Avalanche.Tests;

public sealed class FtsQueryBuilderTests
{
    [Fact]
    public void StopwordOnlyQuestionKeepsSubstantiveTerms()
    {
        // "Why does he think that?" previously ORed every token including
        // stopwords; now only the content word survives.
        var q = FtsQueryBuilder.Build("Why does he think that?");
        Assert.Equal("\"think\"", q);
    }

    [Fact]
    public void MixedQueryKeepsContentTermsInOrder()
    {
        var q = FtsQueryBuilder.Build("What fees apply to the trial?");
        Assert.Equal("\"fees\" OR \"apply\" OR \"trial\"", q);
    }

    [Fact]
    public void ApostrophesAndHyphensSplitIntoTokens()
    {
        var q = FtsQueryBuilder.Build("don't split well-known");
        Assert.Equal("\"don\" OR \"split\" OR \"well\" OR \"known\"", q);
    }

    [Fact]
    public void FtsOperatorsCannotLeakThrough()
    {
        var q = FtsQueryBuilder.Build("NEAR(\"x\") AND *");
        Assert.Equal("\"near\"", q);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the of and to is it")]
    public void NothingSearchableYieldsNull(string? query)
    {
        Assert.Null(FtsQueryBuilder.Build(query));
    }

    [Fact]
    public void DuplicateTermsAreCollapsed()
    {
        var q = FtsQueryBuilder.Build("fees fees FEES the fees");
        Assert.Equal("\"fees\"", q);
    }
}
