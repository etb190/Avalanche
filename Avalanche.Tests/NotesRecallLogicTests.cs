using System.Collections.Generic;
using Avalanche.Features.Notes;
using Xunit;

public sealed class NotesRecallLogicTests
{
    [Fact]
    public void ChunkBoundaries_Slice_80_200_Into_Three_Blocks()
    {
        var blocks = NotesRecallLogic.ChunkBoundaries(80, 200);
        Assert.Equal(3, blocks.Count);
        Assert.Equal((80, 129), blocks[0]);
        Assert.Equal((130, 179), blocks[1]);
        Assert.Equal((180, 200), blocks[2]);
    }

    [Fact]
    public void ChunkBoundaries_Small_Range_Stays_Whole()
    {
        Assert.Single(NotesRecallLogic.ChunkBoundaries(1, 1));
        Assert.Single(NotesRecallLogic.ChunkBoundaries(10, 15));
        Assert.Equal((4, 53), NotesRecallLogic.ChunkBoundaries(4, 53)[0]);
    }

    [Fact]
    public void ChunkBoundaries_Clamps_Inverted_Input()
    {
        var blocks = NotesRecallLogic.ChunkBoundaries(9, 3);
        Assert.Single(blocks);
        Assert.Equal((3, 9), blocks[0]);
    }

    [Fact]
    public void ParseCards_Splits_On_Heading_Lines()
    {
        const string reply = """
            ## Pages 80 - 129

            1. CORE ARC: cities rose on the flood plain.
            - [pp. 80-100] irrigation came first.

            ## Pages 130 - 179

            1. CORE ARC: empires consolidated the plain.
            - [pp. 130-150] tribute lists the temples.

            ## Pages 180 - 200

            1. CORE ARC: the tail block keeps its budget.
            """;
        var cards = NotesRecallLogic.ParseCards(reply, 80, 200);
        Assert.Equal(3, cards.Count);
        Assert.Equal((80, 129), (cards[0].FirstPage, cards[0].LastPage));
        Assert.Equal((130, 179), (cards[1].FirstPage, cards[1].LastPage));
        Assert.Equal((180, 200), (cards[2].FirstPage, cards[2].LastPage));
        Assert.Contains("CORE ARC: cities rose", cards[0].Content);
        Assert.DoesNotContain("## Pages", cards[0].Content);
        Assert.Contains("tribute lists the temples", cards[1].Content);
    }

    [Fact]
    public void ParseCards_Tolerates_En_Dash_And_Pages_Plural()
    {
        const string reply = "### Pages 80 \u2013 129\n\nOnly one card here.\n";
        var cards = NotesRecallLogic.ParseCards(reply, 80, 200);
        var card = Assert.Single(cards);
        Assert.Equal((80, 129), (card.FirstPage, card.LastPage));
        Assert.Equal("Only one card here.", card.Content);
    }

    [Fact]
    public void ParseCards_Drops_Preamble_Before_First_Heading()
    {
        const string reply = "Sure, here are the notes:\n## Pages 1 - 50\n\nThe actual note.\n";
        var cards = NotesRecallLogic.ParseCards(reply, 1, 50);
        var card = Assert.Single(cards);
        Assert.Equal("The actual note.", card.Content);
    }

    [Fact]
    public void ParseCards_Fallback_Wraps_Headless_Reply_As_One_Card()
    {
        const string reply = "The model ignored the heading contract but wrote a real note.";
        var cards = NotesRecallLogic.ParseCards(reply, 12, 61);
        var card = Assert.Single(cards);
        Assert.Equal((12, 61), (card.FirstPage, card.LastPage));
        Assert.Equal(reply, card.Content);
    }

    [Fact]
    public void ParseCards_Empty_Reply_Yields_No_Cards()
    {
        Assert.Empty(NotesRecallLogic.ParseCards("", 1, 50));
        Assert.Empty(NotesRecallLogic.ParseCards("   \n  ", 1, 50));
    }

    [Fact]
    public void ParseCards_Normalizes_Reversed_Ranges()
    {
        // within a card, first/last normalize; card order stays encounter order
        const string reply = "## Pages 130 - 179\n\nnote one\n## Pages 80 - 129\n\nnote two\n";
        var cards = NotesRecallLogic.ParseCards(reply, 80, 179);
        Assert.Equal(2, cards.Count);
        Assert.Equal((130, 179), (cards[0].FirstPage, cards[0].LastPage));
        Assert.Contains("note one", cards[0].Content);
        Assert.Equal((80, 129), (cards[1].FirstPage, cards[1].LastPage));
        Assert.Contains("note two", cards[1].Content);
    }
}
