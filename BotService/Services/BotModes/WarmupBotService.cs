using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using BotService.Services.Content;
using BotService.Services.Conversation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BotService.Services.BotModes;

/// <summary>
/// Activates when real user count is below threshold.
/// Ensures new users always find people to match with, keeping the app
/// feeling alive even during low-traffic periods.
/// Bots in warmup mode swipe right more aggressively and respond to
/// messages quickly.
///
/// Uses IConversationEngine (hybrid/llm/canned) for message generation.
/// Instrumented via DatingAppApiClient.SetBotContext() for observer tracking.
///
/// SAFETY GUARDS (same as SyntheticUserService):
/// - Checks blocked-by set before messaging
/// - Respects per-user message caps
/// - Skips unresponsive users (48h cooldown)
/// </summary>
public class WarmupBotService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WarmupBotService> _logger;
    private readonly IOptionsMonitor<BotServiceOptions> _config;
    private readonly BotPersonaEngine _personaEngine;
    private readonly MessageContentProvider _messageProvider;
    private readonly IConversationEngine _conversationEngine;

    private const int MaxUnansweredMessages = 5;

    public WarmupBotService(
        IServiceProvider serviceProvider,
        ILogger<WarmupBotService> logger,
        IOptionsMonitor<BotServiceOptions> config,
        BotPersonaEngine personaEngine,
        MessageContentProvider messageProvider,
        IConversationEngine conversationEngine)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
        _personaEngine = personaEngine;
        _messageProvider = messageProvider;
        _conversationEngine = conversationEngine;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WarmupBotService starting");
        await Task.Delay(TimeSpan.FromSeconds(_config.CurrentValue.StartupDelaySec + 10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _config.CurrentValue;
            if (!opts.Enabled || !opts.Modes.Warmup.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                continue;
            }

            try
            {
                await RunWarmupCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Warmup cycle error");
            }

            await Task.Delay(TimeSpan.FromSeconds(opts.Modes.Warmup.CheckIntervalSec), stoppingToken);
        }

        _logger.LogInformation("WarmupBotService stopped");
    }

    private async Task RunWarmupCycleAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var warmupOpts = _config.CurrentValue.Modes.Warmup;

        // Check if warmup is needed based on active bot count as proxy
        var activeBots = await db.BotStates
            .Where(b => b.Status == BotStatus.Active)
            .ToListAsync(ct);

        if (activeBots.Count == 0)
        {
            _logger.LogDebug("No active warmup bots provisioned");
            return;
        }


        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();

        _logger.LogInformation("🌡️ Warmup cycle: {Count} active bots", activeBots.Count);

        foreach (var bot in activeBots)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var personas = _personaEngine.Personas;
                var persona = personas.FirstOrDefault(p => p.Id == bot.PersonaId);
                if (persona == null || !persona.Modes.Contains("warmup")) continue;

                // Set observer context for this bot
                apiClient.SetBotContext(bot.PersonaId, bot.KeycloakUserId ?? "unknown");

                // Refresh blocked-by set
                if (bot.AccessToken != null)
                {
                    var blockedIds = await apiClient.GetBlockedByIdsAsync(bot.AccessToken, ct);
                    bot.SetBlockedByIds(blockedIds);
                }

                // Warmup bots always swipe right and respond immediately
                await WarmupSwipeAsync(bot, persona, apiClient, ct);
                await WarmupRespondAsync(bot, persona, apiClient, ct);
                
                bot.LastAction = "warmup-cycle";
                bot.LastActionAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Warmup cycle error for bot {BotId}", bot.PersonaId);
            }
        }
    }

    private async Task WarmupSwipeAsync(BotState bot, BotPersona persona, DatingAppApiClient apiClient, CancellationToken ct)
    {
        if (bot.AccessToken == null || bot.ProfileId == null) return;
        bot.ResetDailyCountersIfNeeded();

        if (bot.SwipesToday >= (persona.Behavior?.MaxDailySwipes ?? 50)) return;

        var candidates = await apiClient.GetCandidatesAsync(bot.ProfileId.Value, bot.AccessToken, ct);
        if (candidates.Length == 0) return;

        // Warmup bots swipe right on everyone (making sure new users get matches)
        var toSwipe = candidates.Take(3).ToArray();
        foreach (var candidate in toSwipe)
        {
            var targetId = candidate.GetProperty("id").GetInt32();
            
            // Warmup = 90% right swipe
            var isLike = Random.Shared.NextDouble() < 0.9;
            var result = await apiClient.SwipeAsync(bot.ProfileId.Value, targetId, isLike, bot.AccessToken, ct);

            bot.SwipesToday++;

            if (result.IsMutualMatch)
            {
                bot.MatchCount++;
                _logger.LogInformation("🌡️ Warmup MATCH! Bot {BotId} matched with {Target}", bot.PersonaId, targetId);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    private async Task WarmupRespondAsync(BotState bot, BotPersona persona, DatingAppApiClient apiClient, CancellationToken ct)
    {
        if (bot.AccessToken == null) return;

        // Get conversations and respond to any unread messages quickly
        var conversations = await apiClient.GetConversationsAsync(bot.AccessToken, ct);
        if (conversations.Length == 0) return;

        foreach (var conv in conversations.Take(5))
        {
            try
            {
                if (!conv.TryGetProperty("lastMessage", out var lastMsg)) continue;
                if (!lastMsg.TryGetProperty("senderId", out var senderProp)) continue;
                
                var senderId = senderProp.GetString();
                if (senderId == bot.KeycloakUserId) continue; // We sent the last message

                if (string.IsNullOrEmpty(senderId)) continue;

                // ─── Safety guard: skip if user blocked us ─────────
                if (bot.IsBlockedBy(senderId))
                {
                    _logger.LogDebug("🌡️ Warmup bot {BotId}: skipping {User} — blocked",
                        bot.PersonaId, senderId);
                    continue;
                }

                // ─── Conversation guard: skip unresponsive users ───
                if (bot.IsUnresponsive(senderId))
                {
                    _logger.LogDebug("🌡️ Warmup bot {BotId}: skipping {User} — unresponsive",
                        bot.PersonaId, senderId);
                    continue;
                }

                // ─── Conversation guard: cap per-user messages ─────
                var sentCount = bot.GetMessageCountForUser(senderId);
                if (sentCount >= MaxUnansweredMessages)
                {
                    bot.MarkUnresponsive(senderId);
                    continue;
                }

                // ─── Generate message via Conversation Engine (LLM/hybrid/canned) ─────
                string message;
                try
                {
                    var recentMessages = await apiClient.GetConversationMessagesAsync(
                        senderId, bot.AccessToken, ct);

                    var context = new ConversationContext
                    {
                        Persona = persona,
                        BotUserId = bot.KeycloakUserId ?? "",
                        MatchedUserId = senderId,
                        MessageCount = bot.ConversationCount,
                        RecentMessages = recentMessages
                    };

                    var reply = await _conversationEngine.GenerateReplyAsync(context, ct);
                    message = reply.Message;

                    _logger.LogDebug("🌡️ Warmup bot {BotId}: {Source} reply ({Provider}, {Tokens}tok, {Latency}ms)",
                        bot.PersonaId, reply.Source, reply.Provider ?? "n/a", reply.TokensUsed, reply.LatencyMs);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "🌡️ Warmup bot {BotId}: LLM failed, canned fallback", bot.PersonaId);
                    message = _messageProvider.GetMessageForDepth(bot.MessagesSentToday);
                }

                await apiClient.SendMessageAsync(senderId, message, bot.AccessToken, ct);
                bot.MessagesSentToday++;
                bot.ConversationCount++;
                bot.IncrementMessageCount(senderId);

                _logger.LogDebug("🌡️ Warmup bot {BotId} responded to {User}", bot.PersonaId, senderId);
                
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Warmup respond error for conversation");
            }
        }
    }
}
