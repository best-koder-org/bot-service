using BotService.Configuration;
using BotService.Models;
using BotService.Services.Content;
using BotService.Services.Conversation;
using BotService.Services.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BotService.Tests.Services;

public class HybridConversationEngineTests
{
    private readonly Mock<ILogger<HybridConversationEngine>> _logger = new();
    private readonly Mock<ILogger<CannedConversationEngine>> _cannedLogger = new();
    private readonly Mock<ILogger<LlmConversationEngine>> _llmLogger = new();

    private static ConversationContext CreateContext(int messageCount) => new()
    {
        Persona = new BotPersona
        {
            Id = "test-hybrid",
            FirstName = "Hybrid",
            Age = 30,
            City = "Göteborg",
            Occupation = "Utvecklare",
            Interests = new List<string> { "kod", "musik" },
            Behavior = new BotBehavior { Chattiness = "medium" }
        },
        BotUserId = "bot-hybrid",
        MatchedUserId = "human-001",
        MessageCount = messageCount,
        RecentMessages = new List<ChatMessage>()
    };

    private static IOptions<BotServiceOptions> CreateOptions(int threshold = 3) =>
        Options.Create(new BotServiceOptions
        {
            Llm = new LlmOptions
            {
                PrimaryProvider = "test",
                FallbackProvider = "test",
                DailyTokenBudget = 500_000,
                MaxTokensPerMessage = 150,
                Temperature = 0.7
            },
            Conversation = new ConversationOptions
            {
                Engine = "hybrid",
                HybridLlmThreshold = threshold,
                MaxContextMessages = 20,
                MaxGuardrailRetries = 2
            }
        });

    private CannedConversationEngine CreateCannedEngine() =>
        new(new MessageContentProvider(new Mock<ILogger<MessageContentProvider>>().Object), _cannedLogger.Object);

    // Create a mock-based LlmConversationEngine stand-in.
    // We can't easily construct the real one (needs LlmRouter etc),
    // so we test via a wrapper approach: verify which engine the Hybrid calls.

    /// <summary>
    /// Creates a HybridConversationEngine where LLM engine is replaced with
    /// a fake that returns identifiable replies, so we can verify routing.
    /// </summary>
    private (HybridConversationEngine hybrid, Mock<LlmConversationEngine> llmMock) CreateHybridWithMockedLlm(int threshold = 3)
    {
        // Since LlmConversationEngine is not easily mockable (no interface on methods),
        // we use a different approach: create two separate engines and check output.
        // The canned engine is real; the LLM engine is mocked at the IConversationEngine level.

        // Actually, HybridConversationEngine takes concrete types (CannedConversationEngine, LlmConversationEngine).
        // Since LlmConversationEngine.GenerateReplyAsync is not virtual and it's a concrete class,
        // we can't mock it with Moq directly.
        //
        // Instead, we'll test the threshold behavior by checking the output source:
        // - canned engine always returns source="canned"
        // - If we could call LLM, it would return source="llm" or "llm_fallback_canned"
        //
        // For this test, we create a real LlmConversationEngine with a mock router that
        // always returns a success response, so we can distinguish canned vs LLM output.

        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.ProviderName).Returns("test-mock");
        mockProvider.Setup(p => p.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = "Hej från LLM!",
                Success = true,
                Provider = "test-mock",
                TokensUsed = 10,
                LatencyMs = 50
            });

        var options = CreateOptions(threshold);
        var router = new LlmRouter(
            new ILlmProvider[] { mockProvider.Object },
            Options.Create(new BotServiceOptions
            {
                Llm = new LlmOptions { PrimaryProvider = "test-mock", FallbackProvider = "test-mock", DailyTokenBudget = 500_000 }
            }),
            new Mock<ILogger<LlmRouter>>().Object);

        var cannedEngine = CreateCannedEngine();
        var llmEngine = new LlmConversationEngine(router, cannedEngine, options, _llmLogger.Object);

        var hybrid = new HybridConversationEngine(cannedEngine, llmEngine, options, _logger.Object);
        return (hybrid, null!);
    }

    // ── Below threshold → canned ─────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GenerateReplyAsync_BelowThreshold_UsesCanned(int messageCount)
    {
        var (hybrid, _) = CreateHybridWithMockedLlm(threshold: 3);
        var reply = await hybrid.GenerateReplyAsync(CreateContext(messageCount));

        Assert.Equal("canned", reply.Source);
        Assert.Equal(0, reply.TokensUsed);
    }

    // ── At/above threshold → LLM ────────────────────────────────────

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task GenerateReplyAsync_AtOrAboveThreshold_UsesLlm(int messageCount)
    {
        var (hybrid, _) = CreateHybridWithMockedLlm(threshold: 3);
        var reply = await hybrid.GenerateReplyAsync(CreateContext(messageCount));

        Assert.Equal("llm", reply.Source);
        Assert.Equal("Hej från LLM!", reply.Message);
    }

    // ── Threshold of 0 → always LLM ─────────────────────────────────

    [Fact]
    public async Task GenerateReplyAsync_ThresholdZero_AlwaysUsesLlm()
    {
        var (hybrid, _) = CreateHybridWithMockedLlm(threshold: 0);
        var reply = await hybrid.GenerateReplyAsync(CreateContext(0));

        Assert.Equal("llm", reply.Source);
    }

    // ── High threshold → always canned ───────────────────────────────

    [Fact]
    public async Task GenerateReplyAsync_HighThreshold_AlwaysCanned()
    {
        var (hybrid, _) = CreateHybridWithMockedLlm(threshold: 1000);
        var reply = await hybrid.GenerateReplyAsync(CreateContext(50));

        Assert.Equal("canned", reply.Source);
    }

    // ── Exact boundary ───────────────────────────────────────────────

    [Fact]
    public async Task GenerateReplyAsync_ExactlyAtThreshold_UsesLlm()
    {
        var (hybrid, _) = CreateHybridWithMockedLlm(threshold: 5);
        
        var belowReply = await hybrid.GenerateReplyAsync(CreateContext(4));
        Assert.Equal("canned", belowReply.Source);

        var atReply = await hybrid.GenerateReplyAsync(CreateContext(5));
        Assert.Equal("llm", atReply.Source);
    }

    // ── Reply always has a message ──────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10)]
    public async Task GenerateReplyAsync_AlwaysReturnsMessage(int messageCount)
    {
        var (hybrid, _) = CreateHybridWithMockedLlm(threshold: 3);
        var reply = await hybrid.GenerateReplyAsync(CreateContext(messageCount));

        Assert.NotNull(reply);
        Assert.False(string.IsNullOrWhiteSpace(reply.Message));
    }

    // ── LLM fallback to canned on provider failure ───────────────────

    [Fact]
    public async Task GenerateReplyAsync_LlmFails_FallsBackToCanned()
    {
        // Create a router where all providers fail
        var failProvider = new Mock<ILlmProvider>();
        failProvider.Setup(p => p.ProviderName).Returns("fail-provider");
        failProvider.Setup(p => p.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LlmResponse.Failure("fail-provider", "network_error"));

        var options = CreateOptions(threshold: 0); // Always try LLM
        var router = new LlmRouter(
            new ILlmProvider[] { failProvider.Object },
            Options.Create(new BotServiceOptions
            {
                Llm = new LlmOptions { PrimaryProvider = "fail-provider", FallbackProvider = "fail-provider" }
            }),
            new Mock<ILogger<LlmRouter>>().Object);

        var cannedEngine = CreateCannedEngine();
        var llmEngine = new LlmConversationEngine(router, cannedEngine, options, _llmLogger.Object);
        var hybrid = new HybridConversationEngine(cannedEngine, llmEngine, options, _logger.Object);

        var reply = await hybrid.GenerateReplyAsync(CreateContext(5));

        // LLM failed → LlmConversationEngine falls back to canned internally
        Assert.Equal("llm_fallback_canned", reply.Source);
        Assert.False(string.IsNullOrWhiteSpace(reply.Message));
    }
}
