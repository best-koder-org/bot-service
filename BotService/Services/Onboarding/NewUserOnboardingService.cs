using System.Text.Json;
using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using BotService.Services.Keycloak;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BotService.Services.Onboarding;

/// <summary>
/// Automatically onboards freshly-signed-up human testers: picks compatible active bots
/// that pre-like the new tester, so the tester experiences quick matches. Each tester is
/// assisted only once (tracked in OnboardingTargets).
/// </summary>
public class NewUserOnboardingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<BotServiceOptions> _config;
    private readonly DemoRuntimeState _demoState;
    private readonly ILogger<NewUserOnboardingService> _logger;

    public NewUserOnboardingService(
        IServiceProvider serviceProvider,
        IOptionsMonitor<BotServiceOptions> config,
        DemoRuntimeState demoState,
        ILogger<NewUserOnboardingService> logger)
    {
        _serviceProvider = serviceProvider;
        _config = config;
        _demoState = demoState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for bots to provision before scanning for new testers.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var demo = _config.CurrentValue.Demo;
            if ((_demoState.Enabled || demo.Enabled) && demo.OnboardingCheckIntervalSec > 0)
            {
                try
                {
                    await OnboardNewTestersAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Onboarding check failed");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, demo.OnboardingCheckIntervalSec)), stoppingToken);
        }
    }

    private async Task OnboardNewTestersAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();
        var keycloak = scope.ServiceProvider.GetRequiredService<KeycloakBotProvisioner>();
        var personaEngine = scope.ServiceProvider.GetRequiredService<BotPersonaEngine>();

        var demoPersona = personaEngine.GetPersonaById("demo-user");
        if (demoPersona == null) return;

        var (token, _, _) = await keycloak.GetBotTokenAsync(demoPersona, ct);
        if (string.IsNullOrEmpty(token)) return;

        // Only scan a bounded recent window so we don't re-onboard old history every run.
        var since = DateTime.UtcNow.AddMinutes(-10).ToString("O");
        var candidates = await apiClient.GetOnboardingCandidatesAsync(token, since, ct);
        if (candidates.Length == 0) return;

        var assistedSet = new HashSet<string>(
            await db.OnboardingTargets.Select(t => t.KeycloakUserId).ToListAsync(ct));

        var activeBots = await db.BotStates
            .Where(b => b.Status == BotStatus.Active && b.AccessToken != null && b.ProfileId != null)
            .ToListAsync(ct);
        var maxOnboard = Math.Max(1, _config.CurrentValue.Demo.MaxOnboardingBots);

        foreach (var candidate in candidates)
        {
            if (!TryParseCandidate(candidate, out var keycloakId, out var profileId)) continue;
            if (assistedSet.Contains(keycloakId)) continue;

            var chosen = activeBots
                .Where(b => b.PersonaId != "demo-user")
                .OrderBy(_ => Guid.NewGuid())
                .Take(maxOnboard)
                .ToList();

            foreach (var bot in chosen)
            {
                if (bot.ProfileId == null || bot.AccessToken == null) continue;

                var persona = personaEngine.GetPersonaById(bot.PersonaId);
                if (persona == null) continue;

                // Refresh token if expired so the swipe authenticates.
                if (bot.TokenExpiresAt <= DateTime.UtcNow)
                {
                    var (acc, _, exp) = await keycloak.GetBotTokenAsync(persona, ct);
                    bot.AccessToken = acc;
                    bot.TokenExpiresAt = exp;
                }

                await apiClient.SwipeAsync(bot.ProfileId.Value, profileId, true, bot.AccessToken, ct);
            }

            db.OnboardingTargets.Add(new OnboardingTarget
            {
                KeycloakUserId = keycloakId,
                ProfileId = profileId,
                AssistedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Onboarding assist: {Count} bots pre-liked new tester {ProfileId}",
                chosen.Count, profileId);
        }
    }

    private static bool TryParseCandidate(JsonElement candidate, out string keycloakId, out int profileId)
    {
        keycloakId = string.Empty;
        profileId = 0;

        if (candidate.TryGetProperty("keycloakId", out var kc) && kc.ValueKind == JsonValueKind.String)
            keycloakId = kc.GetString() ?? string.Empty;

        if (candidate.TryGetProperty("profileId", out var pid))
        {
            if (pid.ValueKind == JsonValueKind.Number) profileId = pid.GetInt32();
            else if (pid.ValueKind == JsonValueKind.String) int.TryParse(pid.GetString(), out profileId);
        }

        return !string.IsNullOrEmpty(keycloakId) && profileId > 0;
    }
}
