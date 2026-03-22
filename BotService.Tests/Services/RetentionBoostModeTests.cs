using Microsoft.Extensions.DependencyInjection;
using BotService.Data;
using BotService.Models;
using BotService.Services.Swarm.Modes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotService.Tests.Services;

/// <summary>
/// Tests for RetentionBoostMode — verify mode metadata, initialization,
/// and graceful behavior when no bots are available.
/// </summary>
public class RetentionBoostModeTests
{
    [Fact]
    public void Name_ReturnsRetention()
    {
        var mode = CreateMode();
        Assert.Equal("retention", mode.Name);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        var mode = CreateMode();
        Assert.False(string.IsNullOrWhiteSpace(mode.Description));
    }

    [Fact]
    public async Task InitializeAsync_EmptyParameters_DoesNotThrow()
    {
        var mode = CreateMode();
        await mode.InitializeAsync(new Dictionary<string, string>());
    }

    [Fact]
    public async Task RunAsync_NoBots_CompletesGracefully()
    {
        var mode = CreateModeWithDb(out _);
        await mode.InitializeAsync(new Dictionary<string, string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await mode.RunAsync(5, cts.Token);
        // No exception = pass — mode should log warning and return
    }

    [Fact]
    public async Task RunAsync_CancellationRequested_Stops()
    {
        var mode = CreateModeWithDb(out _);
        await mode.InitializeAsync(new Dictionary<string, string>());

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel
        // Should throw OperationCanceledException or complete immediately
        try
        {
            await mode.RunAsync(5, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_DoesNotThrow()
    {
        var mode = CreateMode();
        await mode.StopAsync();
    }

    private static RetentionBoostMode CreateMode()
    {
        var sp = BuildServiceProvider();
        var observer = new BotService.Services.Observer.BotObserver(
            sp, sp.GetRequiredService<ILoggerFactory>().CreateLogger<BotService.Services.Observer.BotObserver>());
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<RetentionBoostMode>();
        return new RetentionBoostMode(sp, observer, logger);
    }

    private static RetentionBoostMode CreateModeWithDb(out BotDbContext db)
    {
        var sp = BuildServiceProvider();
        db = sp.GetRequiredService<BotDbContext>();
        var observer = new BotService.Services.Observer.BotObserver(
            sp, sp.GetRequiredService<ILoggerFactory>().CreateLogger<BotService.Services.Observer.BotObserver>());
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<RetentionBoostMode>();
        return new RetentionBoostMode(sp, observer, logger);
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDbContext<BotDbContext>(o =>
            o.UseInMemoryDatabase($"RetentionTests_{Guid.NewGuid()}"));
        services.AddLogging();
        services.AddSingleton(new BotService.Services.DatingAppApiClient(
            new HttpClient(),
            Microsoft.Extensions.Options.Options.Create(new BotService.Configuration.BotServiceOptions()),
            new Mock<ILogger<BotService.Services.DatingAppApiClient>>().Object));
        services.AddSingleton(new BotService.Services.Content.MessageContentProvider(
            new Mock<ILogger<BotService.Services.Content.MessageContentProvider>>().Object));
        return services.BuildServiceProvider();
    }
}
