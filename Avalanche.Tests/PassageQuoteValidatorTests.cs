using Avalanche.Features.AI;
using Xunit;

namespace Avalanche.Tests;

public sealed class PassageQuoteValidatorTests
{
    private const string Chunk = "The trial lasted twelve weeks. After the first year, the fee doubles and patients may opt out.";

    [Fact]
    public void ExactQuoteIsValid()
    {
        Assert.True(PassageQuoteValidator.IsValidQuote("the fee doubles", Chunk));
    }

    [Fact]
    public void PunctuationAndCaseAndLineBreaksAreForgiven()
    {
        Assert.True(PassageQuoteValidator.IsValidQuote("After the first year,\r\n  the fee doubles!", Chunk));
    }

    [Fact]
    public void InventedQuoteIsRejected()
    {
        Assert.False(PassageQuoteValidator.IsValidQuote("the fee triples after six months", Chunk));
    }

    [Fact]
    public void EmptyOrTinyQuoteIsUnverified()
    {
        Assert.False(PassageQuoteValidator.IsValidQuote("", Chunk));
        Assert.False(PassageQuoteValidator.IsValidQuote("the fee", Chunk));
    }

    [Fact]
    public void MostlyInOrderParaphrasePasses()
    {
        // 4 of 5 words appear in order (one reworded) -> >= 80% accepted.
        Assert.True(PassageQuoteValidator.IsValidQuote("the trial lasted twelve weeks extra", Chunk));
    }

    [Fact]
    public void OutOfOrderWordsAreRejected()
    {
        Assert.False(PassageQuoteValidator.IsValidQuote("patients weeks the opted twelve", Chunk));
    }
}
