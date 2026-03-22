using Microsoft.Extensions.DependencyInjection;
using BotService.Data;
using BotService.Models;
using BotService.Services.Swarm.Modes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotService.Tests.Services;

/// <summary>
/// Tests for OnboardingAssistMode — verify initialization, parameter parsing,
/// and mode metadata (name/description).
/// Full integration tests require live API; these cover unit-testable logic.
/// </summary>
public class OnboardingAssistModeTests
{
    [Fact]
    public void Name_ReturnsOnboarding()
    {
        var mode = CreateMode();
        Assert.Equal("onboarding", mode.Name);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        var mode = CreateMode();
        Assert.False(string.IsNullOrWhiteSpace(mode.Description));
    }

    [Fact]
    public async Task InitializeAsync_WithTargetUserId_AcceptsParameter()
    {
        var mode = CreateMode();
        var parameters = new Dictionary<string, string>
        {
            { "targetUserId", "user-abc-123" }
        };
        // Should not throw
        await mode.InitializeAsync(parameters);
    }

    [Fact]
    public async Task InitializeAsync_WithTargetProfileId_AcceptsParameter()
    {
        var mode = CreateMode();
        var parameters = new Dictionary<string, string>
        {
            { "targetProfileId", "42" }
        };
        await mode.InitializeAsync(parameters);
    }

    [Fact]
    public async Task InitializeAsync_EmptyParameters_AcceptsDefaults()
    {
        var mode = CreateMode();
        await mode.InitializeAsync(new Dictionary<string, string>());
    }

    [Fact]
    public async Task InitializeAsync_InvalidProfileId_DoesNotThrow()
    {
        var mode = CreateMode();
        var parameters = new Dictionary<string, string>
        {
            { "targetProfileId", "not-a-number" }
        };
        // Should silently ignore non-numeric value
        await mode.InitializeAsync(parameters);
    }

    [Fact]
    public async Task RunAsync_NoBots_CompletesWithoutError()
    {
        // With an empty DB (no bots), ExecuteAsync should log warning and return
        var mode = CreateModeWithDb(out var db);
        await mode.InitializeAsync(new Dictionary<string, string>());

        // RunAsync with 0 bots — should complete gracefully
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await mode.RunAsync(5, cts.Token);
    }

    private static OnboardingAssistMode CreateMode()
    {
        var sp = BuildServiceProvider();
        var observer = new BotService.Services.Observer.BotObserver(
            sp, sp.GetRequiredService<ILoggerFactory>().CreateLogger<BotService.Services.Observer.BotObserver>());
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<OnboardingAssistMode>();
        return new OnboardingAssistMode(sp, observer, logger);
    }

    private static OnboardingAssistMode CreateModeWithDb(out BotDbContext db)
    {
        var sp = BuildServiceProvider();
        db = sp.GetRequiredService<BotDbContext>();
        var observer = new BotService.Services.Observer.BotObserver(
            sp, sp.GetRequiredService<ILoggerFactory>().CreateLogger<BotService.Services.Observer.BotObserver>());
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<OnboardingAssistMode>();
        return new OnboardingAssistMode(sp, observer, logger);
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDbContext<BotDbContext>(o =>
            o.UseInMemoryDatabase($"OnboardingTests_{Guid.NewGuid()}"));
        services.AddLogging();
        // DatingAppApiClient + MessageContentProvider registered with minimal stubs
        services.AddSingleton(new BotService.Services.DatingAppApiClient(
            new HttpClient(),
            Microsoft.Extensions.Options.Options.Create(new BotService.Configuration.BotServiceOptions()),
            new Mock<ILogger<BotService.Services.DatingAppApiClient>>().Object));
        services.AddSingleton(new BotService.Services.Content.MessageContentProvider(
            new Mock<ILogger<BotService.Services.Content.MessageContentProvider>>().Object));
        return services.BuildServiceProvider();
    }
}
