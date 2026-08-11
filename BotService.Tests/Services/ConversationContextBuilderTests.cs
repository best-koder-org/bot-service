using BotService.Services.Llm;

namespace BotService.Tests.Services;

public class ConversationContextBuilderTests
{
    private const string BotUserId = "bot-001";
    private const string HumanUserId = "human-001";

    // ── Role mapping ─────────────────────────────────────────────────

    [Fact]
    public void Build_BotMessages_MappedToAssistantRole()
    {
        var messages = new[]
        {
            new ChatMessage { SenderUserId = BotUserId, Content = "Hej!" }
        };

        var result = ConversationContextBuilder.Build(messages, BotUserId);

        Assert.Single(result);
        Assert.Equal("assistant", result[0].Role);
        Assert.Equal("Hej!", result[0].Content);
    }

    [Fact]
    public void Build_HumanMessages_MappedToUserRole()
    {
        var messages = new[]
        {
            new ChatMessage { SenderUserId = HumanUserId, Content = "Hej!" }
        };

        var result = ConversationContextBuilder.Build(messages, BotUserId);

        Assert.Single(result);
        Assert.Equal("user", result[0].Role);
    }

    [Fact]
    public void Build_AlternatingConversation_CorrectRoles()
    {
        var messages = new[]
        {
            new ChatMessage { SenderUserId = HumanUserId, Content = "Hej!" },
            new ChatMessage { SenderUserId = BotUserId, Content = "Hej! Kul att matcha!" },
            new ChatMessage { SenderUserId = HumanUserId, Content = "Hur mår du?" },
            new ChatMessage { SenderUserId = BotUserId, Content = "Bra tack!" },
        };

        var result = ConversationContextBuilder.Build(messages, BotUserId);

        Assert.Equal(4, result.Count);
        Assert.Equal("user", result[0].Role);
        Assert.Equal("assistant", result[1].Role);
        Assert.Equal("user", result[2].Role);
        Assert.Equal("assistant", result[3].Role);
    }

    // ── Chronological order preserved ────────────────────────────────

    [Fact]
    public void Build_PreservesChronologicalOrder()
    {
        var messages = new[]
        {
            new ChatMessage { SenderUserId = HumanUserId, Content = "First" },
            new ChatMessage { SenderUserId = BotUserId, Content = "Second" },
            new ChatMessage { SenderUserId = HumanUserId, Content = "Third" },
        };

        var result = ConversationContextBuilder.Build(messages, BotUserId);

        Assert.Equal("First", result[0].Content);
        Assert.Equal("Second", result[1].Content);
        Assert.Equal("Third", result[2].Content);
    }

    // ── Token truncation ─────────────────────────────────────────────

    [Fact]
    public void Build_ExceedsTokenBudget_TruncatesFromStart()
    {
        // maxTokens=10, ~4 chars/token = ~40 chars budget
        // Create messages that exceed the budget
        var messages = Enumerable.Range(1, 10)
            .Select(i => new ChatMessage
            {
                SenderUserId = i % 2 == 0 ? BotUserId : HumanUserId,
                Content = $"Meddelande nummer {i} som tar lite plats"
            })
            .ToArray();

        var result = ConversationContextBuilder.Build(messages, BotUserId, maxTokens: 10);

        // Should have fewer messages than input due to token limit
        Assert.True(result.Count < messages.Length);
        Assert.True(result.Count >= 1); // Always keeps at least 1
    }

    [Fact]
    public void Build_AlwaysKeepsAtLeastOneMessage()
    {
        // Even if the first message alone blows the budget, it should be kept
        var messages = new[]
        {
            new ChatMessage
            {
                SenderUserId = HumanUserId,
                Content = new string('x', 500) // Way over any token budget
            }
        };

        var result = ConversationContextBuilder.Build(messages, BotUserId, maxTokens: 1);

        Assert.Single(result);
    }

    [Fact]
    public void Build_HighTokenBudget_KeepsAllMessages()
    {
        var messages = Enumerable.Range(1, 5)
            .Select(i => new ChatMessage
            {
                SenderUserId = HumanUserId,
                Content = $"Kort meddelande {i}"
            })
            .ToArray();

        var result = ConversationContextBuilder.Build(messages, BotUserId, maxTokens: 100_000);

        Assert.Equal(5, result.Count);
    }

    // ── Max 20 messages cap ──────────────────────────────────────────

    [Fact]
    public void Build_MoreThan20Messages_TakesLast20()
    {
        var messages = Enumerable.Range(1, 30)
            .Select(i => new ChatMessage
            {
                SenderUserId = HumanUserId,
                Content = $"Msg {i}"
            })
            .ToArray();

        // Use very high token budget so truncation is only by message count
        var result = ConversationContextBuilder.Build(messages, BotUserId, maxTokens: 100_000);

        Assert.True(result.Count <= 20);
        // Should keep the most recent messages
        Assert.Equal("Msg 11", result[0].Content); // 30-20+1 = 11
    }

    // ── Empty input ──────────────────────────────────────────────────

    [Fact]
    public void Build_EmptyMessageList_ReturnsEmptyList()
    {
        var result = ConversationContextBuilder.Build(
            Array.Empty<ChatMessage>(), BotUserId);
        Assert.Empty(result);
    }

    // ── Null content handling ────────────────────────────────────────

    [Fact]
    public void Build_NullContent_TreatedAsEmptyString()
    {
        var messages = new[]
        {
            new ChatMessage { SenderUserId = HumanUserId, Content = null! }
        };

        var result = ConversationContextBuilder.Build(messages, BotUserId);

        Assert.Single(result);
        Assert.Equal("", result[0].Content);
    }

    // ── BuildFromStrings helper ──────────────────────────────────────

    [Fact]
    public void BuildFromStrings_BasicConversation_CorrectOutput()
    {
        var messages = new[]
        {
            ("user", "Hej!"),
            ("assistant", "Hej på dig!"),
            ("user", "Vad gör du?")
        };

        var result = ConversationContextBuilder.BuildFromStrings(messages);

        Assert.Equal(3, result.Count);
        Assert.Equal("user", result[0].Role);
        Assert.Equal("Hej!", result[0].Content);
        Assert.Equal("assistant", result[1].Role);
        Assert.Equal("Hej på dig!", result[1].Content);
    }

    [Fact]
    public void BuildFromStrings_TokenTruncation_Works()
    {
        var messages = Enumerable.Range(1, 20)
            .Select(i => ("user", $"Ett långt meddelande numero {i} med extra text"))
            .ToArray();

        var result = ConversationContextBuilder.BuildFromStrings(messages, maxTokens: 10);

        Assert.True(result.Count < messages.Length);
        Assert.True(result.Count >= 1);
    }
}
