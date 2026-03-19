using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using BotService.Services.Keycloak;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BotService.Services.BotModes;

/// <summary>
/// Runs N concurrent bots at configurable request rate for load testing.
/// Useful for testing how services handle concurrent requests
/// without deploying a full load testing tool like k6.
/// </summary>
public class LoadActorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LoadActorService> _logger;
    private readonly IOptionsMonitor<BotServiceOptions> _config;
    private readonly BotPersonaEngine _personaEngine;

    public LoadActorService(
        IServiceProvider serviceProvider,
        ILogger<LoadActorService> logger,
        IOptionsMonitor<BotServiceOptions> config,
        BotPersonaEngine personaEngine)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
        _personaEngine = personaEngine;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LoadActorService starting");
        await Task.Delay(TimeSpan.FromSeconds(_config.CurrentValue.StartupDelaySec + 20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _config.CurrentValue;
            if (!opts.Enabled || !opts.Modes.Load.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                continue;
            }

            try
            {
                await RunLoadBurstAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load burst error");
            }

            // LoadModeOptions has no CycleIntervalSec; use a sensible default
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }

        _logger.LogInformation("LoadActorService stopped");
    }

    private async Task RunLoadBurstAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();
        var loadOpts = _config.CurrentValue.Modes.Load;

        var activeBots = await db.BotStates
            .Where(b => b.Status == BotStatus.Active && b.AccessToken != null)
            .Take(loadOpts.MaxConcurrentBots)
            .ToListAsync(ct);

        if (activeBots.Count == 0)
        {
            _logger.LogDebug("No active bots available for load testing");
            return;
        }

        // LoadModeOptions has no BurstDurationSec; use a sensible default of 30s
        var burstDurationSec = 30;

        _logger.LogInformation(
            "⚡ Load burst: {BotCount} bots, {Rps} target req/sec, {Duration}s duration",
            activeBots.Count, loadOpts.TargetRequestsPerSecond, burstDurationSec);

        var startTime = DateTime.UtcNow;
        var endTime = startTime.AddSeconds(burstDurationSec);
        var delayBetweenRequests = TimeSpan.FromMilliseconds(1000.0 / loadOpts.TargetRequestsPerSecond);
        
        int totalRequests = 0;
        int successCount = 0;
        int errorCount = 0;
        int rateLimitedCount = 0;

        while (DateTime.UtcNow < endTime && !ct.IsCancellationRequested)
        {
            // Fan out requests across all bots concurrently
            var tasks = activeBots.Select(async bot =>
            {
                try
                {
                    var result = await ExecuteRandomAction(bot, apiClient, ct);
                    Interlocked.Increment(ref totalRequests);
                    
                    if (result == LoadResult.Success) Interlocked.Increment(ref successCount);
                    else if (result == LoadResult.RateLimited) Interlocked.Increment(ref rateLimitedCount);
                    else Interlocked.Increment(ref errorCount);
                }
                catch
                {
                    Interlocked.Increment(ref totalRequests);
                    Interlocked.Increment(ref errorCount);
                }
            });

            await Task.WhenAll(tasks);
            await Task.Delay(delayBetweenRequests, ct);
        }

        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
        var actualRps = totalRequests / elapsed;

        _logger.LogInformation(
            "⚡ Load burst complete: {Total} requests in {Elapsed:F1}s ({Rps:F1} req/s). " +
            "Success: {Success}, Errors: {Errors}, RateLimited: {RateLimited}",
            totalRequests, elapsed, actualRps, successCount, errorCount, rateLimitedCount);
    }

    private async Task<LoadResult> ExecuteRandomAction(BotState bot, DatingAppApiClient apiClient, CancellationToken ct)
    {
        if (bot.AccessToken == null) return LoadResult.Error;

        var action = Random.Shared.Next(4);
        
        try
        {
            switch (action)
            {
                case 0: // Get profile
                    var profile = await apiClient.GetMyProfileAsync(bot.AccessToken, ct);
                    return profile != null ? LoadResult.Success : LoadResult.Error;

                case 1: // Get candidates
                    if (bot.ProfileId == null) return LoadResult.Error;
                    var candidates = await apiClient.GetCandidatesAsync(bot.ProfileId.Value, bot.AccessToken, ct);
                    return candidates != null ? LoadResult.Success : LoadResult.Error;

                case 2: // Swipe
                    if (bot.ProfileId == null) return LoadResult.Error;
                    var swipeResult = await apiClient.SwipeAsync(
                        bot.ProfileId.Value, 
                        Random.Shared.Next(1, 1000),
                        Random.Shared.NextDouble() > 0.5, 
                        bot.AccessToken,
                        ct);
                    // 404/400 is expected for random target IDs, still counts as "service responded"
                    return LoadResult.Success;

                case 3: // Get conversations
                    var convos = await apiClient.GetConversationsAsync(bot.AccessToken, ct);
                    return LoadResult.Success;

                default:
                    return LoadResult.Error;
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            return LoadResult.RateLimited;
        }
    }

    private enum LoadResult
    {
        Success,
        Error,
        RateLimited
    }
}
