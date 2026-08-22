using BotService.Configuration;
using BotService.Services.Keycloak;
using Microsoft.Extensions.Options;

namespace BotService.Services.Onboarding;

/// <summary>
/// Auto-purges stale bot-flagged interactions so demo data never fills the DBs.
/// Runs on a schedule using the TTL (olderThanHours); bots never spam and real-user
/// data is never touched (purge only deletes IsBotGenerated rows).
/// Also exposes PurgeAllAsync so DemoController can wipe all bot data when demo mode stops.
/// </summary>
public class DemoDataPurgeService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<BotServiceOptions> _config;
    private readonly ILogger<DemoDataPurgeService> _logger;

    public DemoDataPurgeService(
        IServiceProvider serviceProvider,
        IOptionsMonitor<BotServiceOptions> config,
        ILogger<DemoDataPurgeService> logger)
    {
        _serviceProvider = serviceProvider;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for bots to provision before the first purge cycle.
        await Task.Delay(TimeSpan.FromSeconds(120), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var ttl = _config.CurrentValue.Demo.PurgeTtlHours;
            // Run roughly once per TTL window, but at least every hour, at most every 6h.
            var intervalHours = ttl > 0 ? Math.Clamp(ttl, 1, 6) : 6;
            await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);

            if (ttl > 0)
            {
                try
                {
                    await PurgeAsync(stoppingToken, ttl);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduled bot-data purge failed");
                }
            }
        }
    }

    /// <summary>Purge bot interactions older than the given TTL (defaults to config).</summary>
    public async Task<bool> PurgeAsync(CancellationToken ct, int? olderThanHours = null)
    {
        var ttl = olderThanHours ?? _config.CurrentValue.Demo.PurgeTtlHours;
        if (ttl <= 0)
        {
            _logger.LogDebug("Purge skipped: PurgeTtlHours={Ttl} (disabled)", ttl);
            return false;
        }

        using var scope = _serviceProvider.CreateScope();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();
        var token = await GetDemoUserTokenAsync(scope, ct);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Purge skipped: could not obtain demo-user token");
            return false;
        }

        var ok = await apiClient.PurgeBotInteractionsAsync(token, ct, ttl);
        _logger.LogInformation("Bot-data purge {Result} (olderThanHours={Ttl})", ok ? "succeeded" : "failed", ttl);
        return ok;
    }

    /// <summary>Purge ALL bot interactions (no TTL filter) — used when demo mode is turned off.</summary>
    public async Task<bool> PurgeAllAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();
        var token = await GetDemoUserTokenAsync(scope, ct);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Full purge skipped: could not obtain demo-user token");
            return false;
        }

        var ok = await apiClient.PurgeBotInteractionsAsync(token, ct, 0);
        _logger.LogInformation("Bot-data full purge {Result}", ok ? "succeeded" : "failed");
        return ok;
    }

    private static async Task<string?> GetDemoUserTokenAsync(IServiceScope scope, CancellationToken ct)
    {
        var keycloak = scope.ServiceProvider.GetRequiredService<KeycloakBotProvisioner>();
        var personaEngine = scope.ServiceProvider.GetRequiredService<BotPersonaEngine>();
        var demoPersona = personaEngine.GetPersonaById("demo-user");
        if (demoPersona == null) return null;

        var (token, _, _) = await keycloak.GetBotTokenAsync(demoPersona, ct);
        return token;
    }
}
