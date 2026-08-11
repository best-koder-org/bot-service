using BotService.Services.Llm;
using BotService.Models;
using BotService.Services.Content;
using BotService.Services.Conversation;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotService.Tests.Services;

public class CannedConversationEngineTests
{
    private readonly CannedConversationEngine _engine;
    private readonly MessageContentProvider _messageProvider;

    public CannedConversationEngineTests()
    {
        _messageProvider = new MessageContentProvider(new Mock<ILogger<MessageContentProvider>>().Object);
        _engine = new CannedConversationEngine(
            _messageProvider,
            new Mock<ILogger<CannedConversationEngine>>().Object);
    }

    private static ConversationContext CreateContext(int messageCount = 0) => new()
    {
        Persona = new BotPersona
        {
            Id = "test-bot",
            FirstName = "TestBot",
            Age = 25,
            City = "Stockholm",
            Occupation = "Testare",
            Interests = new List<string> { "test" },
            Behavior = new BotBehavior { Chattiness = "medium" }
        },
        BotUserId = "bot-001",
        MatchedUserId = "human-001",
        MessageCount = messageCount,
        RecentMessages = new List<BotService.Services.Llm.ChatMessage>()
    };

    [Fact]
    public async Task GenerateReplyAsync_ReturnsNonEmptyMessage()
    {
        var reply = await _engine.GenerateReplyAsync(CreateContext());

        Assert.NotNull(reply);
        Assert.False(string.IsNullOrWhiteSpace(reply.Message));
    }

    [Fact]
    public async Task GenerateReplyAsync_SourceIsCanned()
    {
        var reply = await _engine.GenerateReplyAsync(CreateContext());

        Assert.Equal("canned", reply.Source);
    }

    [Fact]
    public async Task GenerateReplyAsync_ZeroTokensUsed()
    {
        var reply = await _engine.GenerateReplyAsync(CreateContext());

        Assert.Equal(0, reply.TokensUsed);
    }

    [Fact]
    public async Task GenerateReplyAsync_ZeroLatency()
    {
        var reply = await _engine.GenerateReplyAsync(CreateContext());

        Assert.Equal(0, reply.LatencyMs);
    }

    [Fact]
    public async Task GenerateReplyAsync_NullProvider()
    {
        var reply = await _engine.GenerateReplyAsync(CreateContext());

        Assert.Null(reply.Provider);
    }

    [Fact]
    public async Task GenerateReplyAsync_DifferentDepths_ReturnMessages()
    {
        // Verify engine produces messages at various conversation depths
        for (int depth = 0; depth < 10; depth++)
        {
            var reply = await _engine.GenerateReplyAsync(CreateContext(depth));
            Assert.False(string.IsNullOrWhiteSpace(reply.Message),
                $"Empty message at depth {depth}");
        }
    }

    [Fact]
    public async Task GenerateReplyAsync_MessageMatchesContentProvider()
    {
        // Engine should return the exact same message as MessageContentProvider for same depth
        var depth = 5;
        var expected = _messageProvider.GetMessageForDepth(depth);
        var reply = await _engine.GenerateReplyAsync(CreateContext(depth));

        Assert.Equal(expected, reply.Message);
    }
}
