using Avalanche.Features.Summary;
using Xunit;

namespace Avalanche.Tests;

// Regression tests for the deterministic prose guarantee (v1.8.86): whatever the
// model emits, a bullet-list digest must be detectable and mechanically flattenable
// before it can reach the summary window or the cache.

public sealed class SummaryProseGuardTests
{
    [Fact]
    public void ProseDigest_IsNotABulletList()
    {
        string digest = "Sargon of Akkad forged the first empire by defeating Lugal-Zage-si " +
                        "in battle. He then stationed governors in each conquered city.\n\n" +
                        "The empire held for two centuries - stone stelae name his grandson " +
                        "Naram-Sin as the first king to claim divinity.";
        Assert.False(ProseGuard.LooksLikeBulletList(digest));
    }

    [Fact]
    public void BulletDump_IsDetected()
    {
        string digest = "- Sargon defeated Lugal-Zage-si\n" +
                        "- Governors ruled each city\n" +
                        "- The empire lasted two centuries\n" +
                        "- Naram-Sin claimed divinity\n" +
                        "One closing prose line that is not a bullet at all.";
        Assert.True(ProseGuard.LooksLikeBulletList(digest));
    }

    [Fact]
    public void NumberedList_IsDetected()
    {
        string digest = "1. Sargon seized the throne.\n2. He retook the Sumerian cities.\n" +
                        "3. He founded the first empire.\n4. His dynasty ruled for generations.";
        Assert.True(ProseGuard.LooksLikeBulletList(digest));
    }

    [Fact]
    public void ProseWithDashesAndRules_IsNotABulletList()
    {
        string digest = "The silver-based economy - shekels by weight - collapsed twice.\n" +
                        "Trade routes stretched from the Gulf to Anatolia.\n\n" +
                        "A rule follows.\n---\nMore prose about the third dynasty of Ur, and " +
                        "how it fell to the Elamites around 2004 BC.";
        Assert.False(ProseGuard.LooksLikeBulletList(digest));
    }

    [Fact]
    public void EmptyAndNull_AreNotBulletLists()
    {
        Assert.False(ProseGuard.LooksLikeBulletList(null));
        Assert.False(ProseGuard.LooksLikeBulletList(string.Empty));
        Assert.False(ProseGuard.LooksLikeBulletList("   \n  "));
    }

    [Fact]
    public void BoldOpenersAndSignedNumbers_AreNotBullets()
    {
        Assert.False(ProseGuard.IsBulletLine("**Evidence:** the tablets name six cities."));
        Assert.False(ProseGuard.IsBulletLine("---"));
        Assert.False(ProseGuard.IsBulletLine("-3 shekels of silver"));
        Assert.True(ProseGuard.IsBulletLine("- tablets name six cities"));
        Assert.True(ProseGuard.IsBulletLine("• the sacred marriage rite"));
        Assert.True(ProseGuard.IsBulletLine("2) governors replaced the kings"));
    }

    [Fact]
    public void ConvertBullets_JoinsIntoProseUnderHeadings()
    {
        string dump = "### Play and Ritual\n" +
                      "- games reenacted the primeval battle\n" +
                      "- the king renewed fertility by the sacred marriage\n" +
                      "### Afterlife\n" +
                      "- the netherworld lay under the earth\n" +
                      "- the dead drank muddy water";
        string prose = ProseGuard.ConvertBulletsToProse(dump);

        Assert.StartsWith("### Play and Ritual", prose);
        Assert.Contains("### Afterlife", prose);
        Assert.DoesNotContain("\n-", prose);
        Assert.Contains("Games reenacted the primeval battle. The king renewed fertility by the sacred marriage", prose);
        Assert.EndsWith("The netherworld lay under the earth. The dead drank muddy water", prose);
    }

    [Fact]
    public void ConvertNumberedBullets_PreservesContent()
    {
        string prose = ProseGuard.ConvertBulletsToProse(
            "1. Sargon seized the throne.\n2. He retook the Sumerian cities.\n" +
            "3. His dynasty ruled for generations.");

        Assert.DoesNotContain("\n1.", prose);
        Assert.StartsWith("Sargon seized the throne.", prose);
        Assert.Contains("He retook the Sumerian cities. His dynasty ruled for generations.", prose);
    }

    [Fact]
    public void ConvertBullets_KeepsProseLinesIntact()
    {
        string mixed = "Intro prose line one\nwrapped onto two source lines.\n\n" +
                       "- only bullet here\n- second item\n\nClosing prose.";
        string prose = ProseGuard.ConvertBulletsToProse(mixed);

        Assert.Contains("Intro prose line one wrapped onto two source lines.", prose);
        Assert.Contains("Only bullet here. Second item", prose);
        Assert.EndsWith("Closing prose.", prose);
        Assert.DoesNotContain("\n-", prose);
    }

    [Fact]
    public void ConvertBullets_ConnectiveFragmentsFlowIntoOneSentence()
    {
        // Continuation clauses (because..., and...) flow into the same sentence
        // with a comma instead of being chopped into staccato fragments.
        string prose = ProseGuard.ConvertBulletsToProse(
            "- the mint operated for two centuries\n" +
            "- because silver never ran short\n" +
            "- and the temples kept the standard weights");

        Assert.DoesNotContain("\n-", prose);
        Assert.Contains(
            "The mint operated for two centuries, because silver never ran short, " +
            "and the temples kept the standard weights", prose);
    }
}
