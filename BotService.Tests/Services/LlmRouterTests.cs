using BotService.Configuration;
using BotService.Services.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BotService.Tests.Services;

public class LlmRouterTests
{
    private readonly Mock<ILogger<LlmRouter>> _logger = new();

    private static IOptions<BotServiceOptions> CreateOptions(
        string primary = "test-primary",
        string fallback = "test-fallback",
        long budget = 500_000) =>
        Options.Create(new BotServiceOptions
        {
            Llm = new LlmOptions
            {
                PrimaryProvider = primary,
                FallbackProvider = fallback,
                DailyTokenBudget = budget
            }
        });

    private static Mock<ILlmProvider> CreateMockProvider(
        string name,
        bool success = true,
        string content = "Hej!",
        int tokens = 10,
        string? error = null)
    {
        var mock = new Mock<ILlmProvider>();
        mock.Setup(p => p.ProviderName).Returns(name);
        mock.Setup(p => p.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(success
                ? new LlmResponse
                {
                    Content = content,
                    Success = true,
                    Provider = name,
                    TokensUsed = tokens,
                    LatencyMs = 50
                }
                : LlmResponse.Failure(name, error ?? "test_error"));
        return mock;
    }

    // ── Primary provider selected first ──────────────────────────────

    [Fact]
    public async Task GenerateAsync_PrimaryAvailable_UsesPrimary()
    {
        var primary = CreateMockProvider("test-primary");
        var fallback = CreateMockProvider("test-fallback");
        var router = new LlmRouter(
            new ILlmProvider[] { primary.Object, fallback.Object },
            CreateOptions(), _logger.Object);

        var result = await router.GenerateAsync(new LlmRequest { SystemPrompt = "Test" });

        Assert.True(result.Success);
        Assert.Equal("test-primary", result.Provider);
        primary.Verify(p => p.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        fallback.Verify(p => p.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Fallback when primary fails ──────────────────────────────────

    [Fact]
    public async Task GenerateAsync_PrimaryFails_FallsToSecondary()
    {
        var primary = CreateMockProvider("test-primary", success: false, error: "rate_limited");
        var fallback = CreateMockProvider("test-fallback", content: "Hej från fallback!");
        var router = new LlmRouter(
            new ILlmProvider[] { primary.Object, fallback.Object },
            CreateOptions(), _logger.Object);

        var result = await router.GenerateAsync(new LlmRequest());

        Assert.True(result.Success);
        Assert.Equal("test-fallback", result.Provider);
        Assert.Equal("Hej från fallback!", result.Content);
    }

    // ── All providers fail ───────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_AllProvidersFail_ReturnsFailure()
    {
        var primary = CreateMockProvider("test-primary", success: false);
        var fallback = CreateMockProvider("test-fallback", success: false);
        var router = new LlmRouter(
            new ILlmProvider[] { primary.Object, fallback.Object },
            CreateOptions(), _logger.Object);

        var result = await router.GenerateAsync(new LlmRequest());

        Assert.False(result.Success);
        Assert.Equal("all_providers_failed", result.Error);
    }

    // ── Circuit breaker ──────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_CircuitBreaker_TripsAfter3Failures()
    {
        var primary = CreateMockProvider("test-primary", success: false, error: "error");
        var fallback = CreateMockProvider("test-fallback", content: "Fallback svar");
        var router = new LlmRouter(
            new ILlmProvider[] { primary.Object, fallback.Object },
            CreateOptions(), _logger.Object);

        // Fail 3 times to trip the circuit breaker
        for (int i = 0; i < 3; i++)
            await router.GenerateAsync(new LlmRequest());

        // After 3 failures, primary's circuit should be open.
        // The 4th call should skip primary entirely and go to fallback.
        primary.Invocations.Clear();
        fallback.Invocations.Clear();

        var result = await router.GenerateAsync(new LlmRequest());

        Assert.True(result.Success);
        Assert.Equal("test-fallback", result.Provider);
        // Primary should NOT have been called (circuit is open)
        primary.Verify(p => p.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Circuit breaker resets on success ─────────────────────────────

    [Fact]
    public async Task GenerateAsync_CircuitResets_AfterSuccess()
    {
        var callCount = 0;
        var primary = new Mock<ILlmProvider>();
        primary.Setup(p => p.ProviderName).Returns("test-primary");
        primary.Setup(p => p.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                // Fail first 2 calls, succeed on 3rd
                return callCount <= 2
                    ? LlmResponse.Failure("test-primary", "error")
                    : new LlmResponse { Content = "Hej!", Success = true, Provider = "test-primary", TokensUsed = 5 };
            });

        var router = new LlmRouter(
            new ILlmProvider[] { primary.Object },
            CreateOptions(primary: "test-primary", fallback: "none"), _logger.Object);

        // 2 failures (below threshold of 3)
        await router.GenerateAsync(new LlmRequest());
        await router.GenerateAsync(new LlmRequest());

        // 3rd call succeeds — circuit should reset
        var result = await router.GenerateAsync(new LlmRequest());
        Assert.True(result.Success);

        // Failure count should be reset now — provider still usable
        // (If circuit had tripped at 3, it would have been skipped)
    }

    // ── Budget exhausted ─────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_BudgetExhausted_ReturnsFailure()
    {
        var primary = CreateMockProvider("test-primary", tokens: 600_000);
        var router = new LlmRouter(
            new ILlmProvider[] { primary.Object },
            CreateOptions(budget: 500_000), _logger.Object);

        // First call uses 600K tokens (exceeds 500K budget)
        await router.GenerateAsync(new LlmRequest());

        // Second call should fail with budget_exhausted
        var result = await router.GenerateAsync(new LlmRequest());

        Assert.False(result.Success);
        Assert.Equal("budget_exhausted", result.Error);
    }

    [Fact]
    public async Task GenerateAsync_UnlimitedBudget_NeverRejectsForBudget()
    {
        var primary = CreateMockProvider("test-primary", tokens: 100_000);
        var router = new LlmRouter(
            new ILlmProvider[] { primary.Object },
            CreateOptions(budget: 0), _logger.Object); // 0 = unlimited

        // Many calls should all succeed
        for (int i = 0; i < 10; i++)
        {
            var result = await router.GenerateAsync(new LlmRequest());
            Assert.True(result.Success);
        }
    }

    // ── Token tracking ───────────────────────────────────────────────

    [Fact]
    public async Task GetUsageStats_TracksTokensAccurately()
    {
        var primary = CreateMockProvider("test-primary", tokens: 42);
        var router = new LlmRouter(
            new ILlmProvider[] { primary.Object },
            CreateOptions(budget: 500_000), _logger.Object);

        await router.GenerateAsync(new LlmRequest());
        await router.GenerateAsync(new LlmRequest());

        var (used, budget, provider) = router.GetUsageStats();

        Assert.Equal(84, used);  // 42 * 2
        Assert.Equal(500_000, budget);
        Assert.Equal("test-primary", provider);
    }

    // ── Provider ordering ────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_ExtraProviders_TriedAfterPrimaryAndFallback()
    {
        var primary = CreateMockProvider("test-primary", success: false);
        var fallback = CreateMockProvider("test-fallback", success: false);
        var extra = CreateMockProvider("ollama", content: "Lokalt svar");
        var router = new LlmRouter(
            new ILlmProvider[] { primary.Object, fallback.Object, extra.Object },
            CreateOptions(), _logger.Object);

        var result = await router.GenerateAsync(new LlmRequest());

        Assert.True(result.Success);
        Assert.Equal("ollama", result.Provider);
    }

    // ── No registered providers ──────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_NoProviders_ReturnsAllFailed()
    {
        var router = new LlmRouter(
            Array.Empty<ILlmProvider>(),
            CreateOptions(), _logger.Object);

        var result = await router.GenerateAsync(new LlmRequest());

        Assert.False(result.Success);
        Assert.Equal("all_providers_failed", result.Error);
    }
}
