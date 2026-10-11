using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalanche.Features.AI;
using Xunit;

namespace Avalanche.Tests;

// ============================================================
// v1.21.0 - the chat history store: conversations persist, the
// history picker can resurrect them, and restored citations
// re-resolve against a loaded index.
// ============================================================

public sealed class ChatHistoryStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly ChatHistoryStore _store;

    public ChatHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "avchathist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new ChatHistoryStore(Path.Combine(_dir, "chat_history.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ChatMessage Msg(ChatMessage.Role role, string content,
        List<AiSource>? sources = null) => new()
    {
        MessageRole = role,
        Content = content,
        Sources = sources ?? new List<AiSource>(),
    };

    private static AiSource Src(string id, int page, string quote, bool verified = true)
        => new()
        {
            SourceId = id,
            PageNumber = page,
            PageIndex = page - 1,
            Quote = quote,
            Reason = "supports the answer",
            PdfCoordinates = new float[] { 10f, 20f, 300f, 400f },
            WordRange = new int[] { 3, 17 },
            QuoteVerified = verified,
            Location = verified ? AiQuoteLocation.Exact : AiQuoteLocation.Unlocated,
        };

    [Fact]
    public void UpsertThenRead_RoundTripsMessagesAndTimestamps()
    {
        var t = new DateTime(2026, 10, 11, 8, 30, 0, DateTimeKind.Utc);
        var messages = new List<ChatMessage>
        {
            Msg(ChatMessage.Role.User, "What does the fee clause say?"),
            Msg(ChatMessage.Role.Assistant, "The fee doubles after the first year. [SOURCE_1]",
                new List<AiSource> { Src("SOURCE_1", 147, "the fee doubles after the first year") }),
        };
        messages[0].Timestamp = t;
        messages[1].Timestamp = t.AddMinutes(2);

        string id = _store.UpsertConversation(null, ChatHistoryStore.KindDocument,
            @"C:\books\contract.pdf", "contract.pdf", messages);

        Assert.NotEmpty(id);
        var back = _store.GetMessages(id);
        Assert.Equal(2, back.Count);
        Assert.Equal(ChatMessage.Role.User, back[0].MessageRole);
        Assert.Equal("What does the fee clause say?", back[0].Content);
        Assert.Equal(t, back[0].Timestamp);
        Assert.Equal(ChatMessage.Role.Assistant, back[1].MessageRole);
        Assert.Single(back[1].Sources);

        var row = _store.GetConversation(id);
        Assert.NotNull(row);
        Assert.Equal(ChatHistoryStore.KindDocument, row!.Kind);
        Assert.Equal("contract.pdf", row.Title);
        Assert.Equal(@"C:\books\contract.pdf", row.SourceRef);
        Assert.Equal(2, row.MessageCount);
    }

    [Fact]
    public void SourcesJson_RoundTripsEveryFieldThePickerNeeds()
    {
        var src = Src("SOURCE_2", 42, "quoted passage text", verified: false);
        src.Location = AiQuoteLocation.Approximate;
        string json = ChatSourceSnapshots.SerializeSources(new List<AiSource> { src });
        var back = ChatSourceSnapshots.DeserializeSources(json);

        Assert.Single(back);
        var s = back[0];
        Assert.Equal("SOURCE_2", s.SourceId);
        Assert.Equal(42, s.PageNumber);
        Assert.Equal(41, s.PageIndex);
        Assert.Equal("quoted passage text", s.Quote);
        Assert.Equal("supports the answer", s.Reason);
        Assert.NotNull(s.PdfCoordinates);
        Assert.Equal(4, s.PdfCoordinates!.Length);
        Assert.Equal(10f, s.PdfCoordinates[0]);
        Assert.NotNull(s.WordRange);
        Assert.Equal(3, s.WordRange![0]);
        Assert.False(s.QuoteVerified);
        Assert.Equal(AiQuoteLocation.Approximate, s.Location);
    }

    [Fact]
    public void SourcesJson_EmptyAndTornInput_YieldEmptyList()
    {
        Assert.Empty(ChatSourceSnapshots.DeserializeSources(""));
        Assert.Empty(ChatSourceSnapshots.DeserializeSources("not json at all"));
        Assert.Empty(ChatSourceSnapshots.DeserializeSources("{\"torn\":"));
        Assert.Empty(ChatSourceSnapshots.SerializeSources(null));
        Assert.Empty(ChatSourceSnapshots.DeserializeSources(
            ChatSourceSnapshots.SerializeSources(new List<AiSource>())));
    }

    [Fact]
    public void UpsertTwice_UpdatesInPlace_MessageCountNeverDoubles()
    {
        string id = _store.UpsertConversation(null, ChatHistoryStore.KindWeb,
            "https://example.com/article", "Example",
            new List<ChatMessage> { Msg(ChatMessage.Role.User, "first question") });
        _store.UpsertConversation(id, ChatHistoryStore.KindWeb,
            "https://example.com/article", "Example",
            new List<ChatMessage>
            {
                Msg(ChatMessage.Role.User, "first question"),
                Msg(ChatMessage.Role.Assistant, "first answer"),
                Msg(ChatMessage.Role.User, "follow-up"),
            });

        var row = _store.GetConversation(id);
        Assert.NotNull(row);
        Assert.Equal(3, row!.MessageCount);
        Assert.Equal(3, _store.GetMessages(id).Count);
    }

    [Fact]
    public void GetLatestConversation_ReturnsNewestActivityPerContext()
    {
        string older = _store.UpsertConversation(null, ChatHistoryStore.KindDocument,
            @"C:\books\a.pdf", "a.pdf",
            new List<ChatMessage> { Msg(ChatMessage.Role.User, "older talk") });
        string newer = _store.UpsertConversation(null, ChatHistoryStore.KindDocument,
            @"C:\books\a.pdf", "a.pdf",
            new List<ChatMessage> { Msg(ChatMessage.Role.User, "newer talk") });

        Assert.NotEqual(older, newer);

        // Touch the older one afterwards: activity, not creation, decides.
        System.Threading.Thread.Sleep(20);
        _store.UpsertConversation(older, ChatHistoryStore.KindDocument, @"C:\books\a.pdf", "a.pdf",
            new List<ChatMessage>
            {
                Msg(ChatMessage.Role.User, "older talk"),
                Msg(ChatMessage.Role.Assistant, "and a later answer"),
            });

        var latest = _store.GetLatestConversation(ChatHistoryStore.KindDocument, @"C:\books\a.pdf");
        Assert.NotNull(latest);
        Assert.Equal(older, latest!.Id);

        // A different book never shares a conversation.
        Assert.Null(_store.GetLatestConversation(ChatHistoryStore.KindDocument, @"C:\books\other.pdf"));
        // A different kind never shares one either.
        Assert.Null(_store.GetLatestConversation(ChatHistoryStore.KindWeb, @"C:\books\a.pdf"));
    }

    [Fact]
    public void GetSummaries_SortNewestFirst_AndCountMessages()
    {
        _store.UpsertConversation(null, ChatHistoryStore.KindDocument, @"C:\b\one.pdf", "one.pdf",
            new List<ChatMessage> { Msg(ChatMessage.Role.User, "q1") });
        System.Threading.Thread.Sleep(20);
        string second = _store.UpsertConversation(null, ChatHistoryStore.KindWeb,
            "https://x.test/", "X",
            new List<ChatMessage>
            {
                Msg(ChatMessage.Role.User, "q2"),
                Msg(ChatMessage.Role.Assistant, "a2"),
            });

        var summaries = _store.GetSummaries();
        Assert.Equal(2, summaries.Count);
        Assert.Equal(second, summaries[0].Id);          // newest activity first
        Assert.Equal(2, summaries[0].MessageCount);
        Assert.Equal(1, summaries[1].MessageCount);
    }

    [Fact]
    public void DeleteConversation_RemovesRowAndMessages()
    {
        string id = _store.UpsertConversation(null, ChatHistoryStore.KindEditor,
            "Meeting notes", "Meeting notes",
            new List<ChatMessage> { Msg(ChatMessage.Role.User, "summarize this") });
        Assert.NotNull(_store.GetConversation(id));

        _store.DeleteConversation(id);

        Assert.Null(_store.GetConversation(id));
        Assert.Empty(_store.GetMessages(id));
        Assert.Null(_store.GetLatestConversation(ChatHistoryStore.KindEditor, "Meeting notes"));
    }

    [Fact]
    public void EnsureConversation_FindsTheLatestRowInsteadOfCreating()
    {
        string first = _store.EnsureConversation(ChatHistoryStore.KindDocument, @"C:\b\dup.pdf", "dup.pdf");
        _store.UpsertConversation(first, ChatHistoryStore.KindDocument, @"C:\b\dup.pdf", "dup.pdf",
            new List<ChatMessage> { Msg(ChatMessage.Role.User, "hello") });
        string second = _store.EnsureConversation(ChatHistoryStore.KindDocument, @"C:\b\dup.pdf", "dup.pdf");

        Assert.Equal(first, second);
        Assert.Equal(1, _store.GetConversation(first)!.MessageCount);
    }
}

public sealed class ChatHistoryCitationTests
{
    private static DocumentChunk Chunk(int pageIndex, string text)
        => new()
        {
            ChunkId = $"c{pageIndex}",
            Text = text,
            PageIndices = new List<int> { pageIndex },
        };

    [Fact]
    public void ReResolve_LocatesQuoteOnTheRecordedPage_AndFillsTheChunk()
    {
        var chunk = Chunk(146, "Intro paragraph. The fee doubles after the first year of service. More text follows here.");
        var messages = new List<ChatMessage>
        {
            new()
            {
                MessageRole = ChatMessage.Role.Assistant,
                Content = "The fee doubles. [SOURCE_1]",
                Sources = new List<AiSource>
                {
                    new()
                    {
                        SourceId = "SOURCE_1",
                        PageNumber = 147,
                        PageIndex = 146,
                        Quote = "The fee doubles after the first year of service.",
                    },
                },
            },
        };

        int resolved = ChatHistoryCitations.ReResolve(messages, new List<DocumentChunk> { chunk });

        Assert.Equal(1, resolved);
        var s = messages[0].Sources[0];
        Assert.Same(chunk, s.ResolvedChunk);
        Assert.Equal(AiQuoteLocation.Exact, s.Location);
        Assert.True(s.QuoteVerified);
        Assert.Equal(146, s.PageIndex);
        Assert.Equal(147, s.PageNumber);
    }

    [Fact]
    public void ReResolve_PageMiss_FallsBackToAnywhereInTheDocument()
    {
        var chunk = Chunk(900, "The renewal clause lives here: the fee doubles after the first year.");
        var src = new AiSource
        {
            SourceId = "SOURCE_1",
            PageNumber = 3,
            PageIndex = 2,   // wrong page on purpose - page drift repair
            Quote = "the fee doubles after the first year",
        };
        var messages = new List<ChatMessage>
        {
            new() { MessageRole = ChatMessage.Role.Assistant, Content = "a", Sources = new List<AiSource> { src } },
        };

        int resolved = ChatHistoryCitations.ReResolve(messages, new List<DocumentChunk> { chunk });

        Assert.Equal(1, resolved);
        Assert.Same(chunk, src.ResolvedChunk);
        Assert.Equal(900, src.PageIndex);
        Assert.Equal(901, src.PageNumber);
    }

    [Fact]
    public void ReResolve_UnfindableQuote_StaysUnresolved_AndCountsZero()
    {
        var chunk = Chunk(0, "Completely unrelated text about oranges and bicycles.");
        var src = new AiSource { SourceId = "SOURCE_1", Quote = "quantum entanglement of invoices", PageIndex = 0 };
        var messages = new List<ChatMessage>
        {
            new() { MessageRole = ChatMessage.Role.Assistant, Content = "a", Sources = new List<AiSource> { src } },
        };

        int resolved = ChatHistoryCitations.ReResolve(messages, new List<DocumentChunk> { chunk });

        Assert.Equal(0, resolved);
        Assert.Null(src.ResolvedChunk);
    }

    [Fact]
    public void ReResolve_AlreadyResolved_AndEmptyQuotes_AreSkipped()
    {
        var chunk = Chunk(0, "text with the quoted sentence inside it");
        var resolved = new AiSource { SourceId = "SOURCE_1", Quote = "whatever" };
        resolved.ResolvedChunk = chunk;
        var emptyQuote = new AiSource { SourceId = "SOURCE_2", Quote = "" };
        var messages = new List<ChatMessage>
        {
            new()
            {
                MessageRole = ChatMessage.Role.Assistant,
                Content = "a",
                Sources = new List<AiSource> { resolved, emptyQuote },
            },
        };

        int count = ChatHistoryCitations.ReResolve(messages, new List<DocumentChunk> { chunk });

        Assert.Equal(0, count);
        Assert.Same(chunk, resolved.ResolvedChunk);
        Assert.Null(emptyQuote.ResolvedChunk);
    }
}
