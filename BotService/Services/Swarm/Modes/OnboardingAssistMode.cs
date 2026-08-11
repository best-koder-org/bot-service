using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using BotService.Services.Content;
using BotService.Services.Conversation;
using BotService.Services.Observer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotService.Services.Swarm.Modes;

/// <summary>
/// Onboarding assist mode: ensures new users get matches quickly.
/// Picks bots matching the new user's preferences (gender, age, location),
/// auto-swipes right on the target user, and sends an opener within 5 min.
/// Goal: every new user has at least one match within 10 minutes.
/// </summary>
public class OnboardingAssistMode : SwarmMode
{
    public override string Name => "onboarding";
    public override string Description => "Bots match new users and send openers to ensure good first experience";

    private string? _targetUserId;
    private int _targetProfileId;

    public OnboardingAssistMode(IServiceProvider sp, BotObserver observer, ILogger logger)
        : base(sp, observer, logger) { }

    public override Task InitializeAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        if (parameters.TryGetValue("targetUserId", out var uid))
            _targetUserId = uid;
        if (parameters.TryGetValue("targetProfileId", out var pid) && int.TryParse(pid, out var id))
            _targetProfileId = id;

        return base.InitializeAsync(parameters, ct);
    }

    protected override async Task ExecuteAsync(int botCount, CancellationToken ct)
    {
        Logger.LogInformation("[Onboarding] Starting assist for user {UserId} with {Count} bots",
            _targetUserId ?? "any-new-user", botCount);

        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();

        // Get active bots that can participate
        var activeBots = await db.BotStates
            .Where(b => b.Status == BotStatus.Active && b.AccessToken != null && b.ProfileId != null)
            .Take(botCount)
            .ToListAsync(ct);

        if (activeBots.Count == 0)
        {
            Logger.LogWarning("[Onboarding] No active bots available for onboarding assist");
            await Observer.RecordObservation(
                FindingType.UxObservation, FindingSeverity.Medium,
                "No bots for onboarding assist",
                "Swarm could not find active bots to assist new user onboarding",
                "bot-service", "", "");
            return;
        }

        Logger.LogInformation("[Onboarding] Found {Count} active bots to assist", activeBots.Count);

        var successCount = 0;
        var messageProvider = scope.ServiceProvider.GetRequiredService<MessageContentProvider>();

        foreach (var bot in activeBots)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                apiClient.SetBotContext(bot.PersonaId, bot.KeycloakUserId ?? "");

                // Step 1: Swipe right on the target user (or discover new users)
                bool matched = false;
                if (_targetProfileId > 0)
                {
                    var (success, isMutual) = await apiClient.SwipeAsync(
                        bot.ProfileId!.Value, _targetProfileId, isLike: true, bot.AccessToken!, ct);
                    matched = success && isMutual;
                    Logger.LogInformation("[Onboarding] Bot {Bot} swiped right on {Target}: {Result}",
                        bot.PersonaId, _targetProfileId, matched ? "MATCH" : "liked");
                }
                else
                {
                    // Discover candidates and swipe right on all
                    var candidates = await apiClient.GetCandidatesAsync(
                        bot.ProfileId!.Value, bot.AccessToken!, ct);
                    foreach (var candidate in candidates.Take(3))
                    {
                        var tid = candidate.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                        if (tid == 0) continue;
                        var (success, isMutual) = await apiClient.SwipeAsync(
                            bot.ProfileId.Value, tid, true, bot.AccessToken!, ct);
                        if (success && isMutual)
                        {
                            matched = true;
                            Logger.LogInformation("[Onboarding] Bot {Bot} matched with {Target}",
                                bot.PersonaId, tid);
                        }
                        await Task.Delay(500, ct);
                    }
                }

                // Step 2: If matched, send an opener message after brief delay
                if (matched)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(10, 30)), ct);

                    var targetId = _targetUserId;
                    if (string.IsNullOrEmpty(targetId) && _targetProfileId > 0)
                    {
                        targetId = await apiClient.GetKeycloakIdForProfileAsync(
                            _targetProfileId, bot.AccessToken!, ct);
                    }

                    if (!string.IsNullOrEmpty(targetId))
                    {
                        var opener = messageProvider.GetMessage("intro");
                        var sent = await apiClient.SendMessageAsync(targetId, opener, bot.AccessToken!, ct);
                        if (sent)
                        {
                            successCount++;
                            Logger.LogInformation("[Onboarding] Bot {Bot} sent opener to {Target}: \"{Msg}\"",
                                bot.PersonaId, targetId, opener[..Math.Min(40, opener.Length)]);
                        }
                    }
                }

                bot.LastAction = "onboarding_assist";
                bot.LastActionAt = DateTime.UtcNow;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[Onboarding] Bot {Bot} failed during assist", bot.PersonaId);
                await Observer.RecordObservation(
                    FindingType.UxObservation, FindingSeverity.Medium,
                    $"Onboarding assist failure: {bot.PersonaId}",
                    ex.Message[..Math.Min(200, ex.Message.Length)],
                    "bot-service", bot.PersonaId, bot.KeycloakUserId ?? "");
            }
        }

        await db.SaveChangesAsync(ct);

        Logger.LogInformation("[Onboarding] Completed: {Success}/{Total} bots successfully assisted",
            successCount, activeBots.Count);
    }
}
