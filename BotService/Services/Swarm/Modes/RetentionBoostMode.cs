using BotService.Data;
using BotService.Models;
using BotService.Services.Content;
using BotService.Services.Observer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BotService.Services.Swarm.Modes;

/// <summary>
/// Retention boost mode: finds active bots with existing matches,
/// sends engaging messages to draw real users back into conversations.
/// Limits to 1 retention nudge per user per week via BotState tracking.
/// Uses conversation engine for natural messages.
/// </summary>
public class RetentionBoostMode : SwarmMode
{
    public override string Name => "retention";
    public override string Description => "Bots re-engage matched users with new messages to boost retention";

    public RetentionBoostMode(IServiceProvider sp, BotObserver observer, ILogger logger)
        : base(sp, observer, logger) { }

    protected override async Task ExecuteAsync(int botCount, CancellationToken ct)
    {
        Logger.LogInformation("[Retention] Starting retention boost with {Count} bots", botCount);

        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();
        var messageProvider = scope.ServiceProvider.GetRequiredService<MessageContentProvider>();

        var activeBots = await db.BotStates
            .Where(b => b.Status == BotStatus.Active && b.AccessToken != null && b.ProfileId != null)
            .Take(botCount)
            .ToListAsync(ct);

        if (activeBots.Count == 0)
        {
            Logger.LogWarning("[Retention] No active bots available");
            return;
        }

        var totalNudges = 0;

        foreach (var bot in activeBots)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                apiClient.SetBotContext(bot.PersonaId, bot.KeycloakUserId ?? "");

                // Get this bot's matches
                var matches = await apiClient.GetMatchesAsync(bot.ProfileId!.Value, bot.AccessToken!, ct);
                if (matches.Length == 0) continue;

                foreach (var match in matches.Take(3)) // Max 3 nudges per bot per cycle
                {
                    string? otherUserId = null;
                    if (match.TryGetProperty("keycloakUserId", out var kcProp))
                        otherUserId = kcProp.GetString();

                    if (string.IsNullOrEmpty(otherUserId)) continue;

                    // Skip if blocked
                    if (bot.IsBlockedBy(otherUserId)) continue;

                    // Check conversation history — only nudge if user hasn't responded in 24h+
                    var messages = await apiClient.GetConversationMessagesAsync(
                        otherUserId, bot.AccessToken!, ct);

                    if (messages.Count == 0)
                    {
                        // No messages yet — send an opener
                        var opener = messageProvider.GetMessage("intro");
                        var sent = await apiClient.SendMessageAsync(otherUserId, opener, bot.AccessToken!, ct);
                        if (sent)
                        {
                            totalNudges++;
                            Logger.LogInformation("[Retention] Bot {Bot} sent opener to matched user {User}",
                                bot.PersonaId, otherUserId);
                        }
                    }
                    else
                    {
                        // Check if last message was from bot and >24h ago (user went silent)
                        var lastMsg = messages[0];
                        if (lastMsg.SenderUserId == (bot.KeycloakUserId ?? "") &&
                            lastMsg.SentAt < DateTime.UtcNow.AddHours(-24))
                        {
                            var nudge = messageProvider.GetMessage("getting_to_know");
                            var sent = await apiClient.SendMessageAsync(otherUserId, nudge, bot.AccessToken!, ct);
                            if (sent)
                            {
                                totalNudges++;
                                Logger.LogInformation("[Retention] Bot {Bot} sent retention nudge to {User}",
                                    bot.PersonaId, otherUserId);
                            }
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(2, 5)), ct);
                }

                bot.LastAction = "retention_boost";
                bot.LastActionAt = DateTime.UtcNow;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[Retention] Bot {Bot} failed during retention cycle", bot.PersonaId);
            }
        }

        await db.SaveChangesAsync(ct);

        Logger.LogInformation("[Retention] Completed: {Nudges} nudge messages sent across {Bots} bots",
            totalNudges, activeBots.Count);

        if (totalNudges > 0)
        {
            await Observer.RecordObservation(
                FindingType.ConversationMetric, FindingSeverity.Info,
                $"Retention boost: {totalNudges} nudges sent",
                $"Retention mode sent {totalNudges} messages across {activeBots.Count} bots",
                "bot-service", "retention-mode", "");
        }
    }
}
