using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using BotService.Services.Content;
using BotService.Services.Conversation;
using BotService.Services.Keycloak;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BotService.Services.BotModes;

/// <summary>
/// Core bot behavior loop: discover → swipe → match → chat.
/// Each synthetic bot runs through this cycle on configurable intervals,
/// simulating realistic human dating app behavior.
///
/// Uses IConversationEngine (hybrid/llm/canned) for message generation.
/// Instrumented via DatingAppApiClient.SetBotContext() for observer tracking.
///
/// SAFETY GUARDS:
/// - Fetches blocked-by set each cycle → skips blocked users
/// - Tracks per-user message counts → max 5 unanswered messages
/// - Marks unresponsive users → backs off for 48h
/// - Bots never see other bots in discover (ExcludeBotFilter in MatchmakingService)
/// </summary>
public class SyntheticUserService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SyntheticUserService> _logger;
    private readonly IOptionsMonitor<BotServiceOptions> _config;
    private readonly BotPersonaEngine _personaEngine;
    private readonly MessageContentProvider _messageProvider;
    private readonly IConversationEngine _conversationEngine;
    private readonly Random _random = new();

    /// <summary>Max unanswered messages to any single user before flagging unresponsive</summary>
    private const int MaxUnansweredMessages = 5;

    public SyntheticUserService(
        IServiceProvider serviceProvider,
        ILogger<SyntheticUserService> logger,
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
        _logger.LogInformation("SyntheticUserService starting");
        await Task.Delay(TimeSpan.FromSeconds(_config.CurrentValue.StartupDelaySec), stoppingToken);

        // Provision all synthetic personas on first run
        await ProvisionBotsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _config.CurrentValue;
            if (!opts.Enabled || !opts.Modes.Synthetic.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            try
            {
                await RunSyntheticCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Synthetic cycle error, retrying next interval");
            }

            var interval = opts.Modes.Synthetic.CycleIntervalSec;
            // Add jitter: ±25%
            var jitter = _random.Next(-interval / 4, interval / 4);
            await Task.Delay(TimeSpan.FromSeconds(interval + jitter), stoppingToken);
        }

        _logger.LogInformation("SyntheticUserService stopped");
    }

    private async Task ProvisionBotsAsync(CancellationToken ct)
    {
        var personas = _personaEngine.GetPersonasForMode("synthetic");
        if (personas.Count == 0)
        {
            _logger.LogWarning("No synthetic personas loaded");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var keycloak = scope.ServiceProvider.GetRequiredService<KeycloakBotProvisioner>();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();

        foreach (var persona in personas)
        {
            try
            {
                // Check if already provisioned
                var existingState = await db.BotStates
                    .FirstOrDefaultAsync(b => b.PersonaId == persona.Id, ct);

                if (existingState is { Status: BotStatus.Active })
                {
                    // Already active — but check if photo still needs uploading
                    if (!existingState.PhotoUploaded)
                    {
                        var (tok, _, _) = await keycloak.GetBotTokenAsync(persona, ct);
                        await UploadBotPhotoAsync(persona, apiClient, tok, existingState, db, ct);
                    }
                    _logger.LogDebug("Bot {Id} already active, skipping provision", persona.Id);
                    continue;
                }

                // Create/find Keycloak user
                var keycloakId = await keycloak.EnsureBotUserAsync(persona, ct);
                
                // Get token
                var (accessToken, refreshToken, expiresAt) = await keycloak.GetBotTokenAsync(persona, ct);
                
                // Create profile if needed
                int? profileId = existingState?.ProfileId;
                if (profileId == null)
                {
                    profileId = await apiClient.CreateProfileAsync(persona, accessToken, ct);
                    if (profileId == null)
                    {
                        // Profile might already exist (409)
                        var profile = await apiClient.GetMyProfileAsync(accessToken, ct);
                        if (profile != null && profile.Value.TryGetProperty("id", out var idEl))
                            profileId = idEl.GetInt32();
                    }
                }

                // Upsert bot state
                if (existingState != null)
                {
                    existingState.KeycloakUserId = keycloakId;
                    existingState.ProfileId = profileId;
                    existingState.AccessToken = accessToken;
                    existingState.RefreshToken = refreshToken;
                    existingState.TokenExpiresAt = expiresAt;
                    existingState.Status = BotStatus.Active;
                }
                else
                {
                    db.BotStates.Add(new BotState
                    {
                        PersonaId = persona.Id,
                        KeycloakUserId = keycloakId,
                        ProfileId = profileId,
                        AccessToken = accessToken,
                        RefreshToken = refreshToken,
                        TokenExpiresAt = expiresAt,
                        Status = BotStatus.Active,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Provisioned synthetic bot: {Id} (profile={ProfileId})",
                    persona.Id, profileId);

                // Upload profile photo if not yet done
                var state = existingState ?? await db.BotStates
                    .FirstOrDefaultAsync(b => b.PersonaId == persona.Id, ct);
                if (state != null && !state.PhotoUploaded)
                {
                    await UploadBotPhotoAsync(persona, apiClient, accessToken, state, db, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to provision bot {Id}", persona.Id);
            }
        }
    }


    /// <summary>Upload a persona photo to photo-service (only once per bot)</summary>
    private async Task UploadBotPhotoAsync(
        BotPersona persona, DatingAppApiClient apiClient, string token,
        BotState state, BotDbContext db, CancellationToken ct)
    {
        try
        {
            // Look for photo file: Personas/photos/{personaId}.png
            var photoPath = Path.Combine(AppContext.BaseDirectory, "Personas", "photos", $"{persona.Id}.png");
            if (!File.Exists(photoPath))
            {
                _logger.LogDebug("No photo file for bot {Id} at {Path}", persona.Id, photoPath);
                return;
            }

            var imageBytes = await File.ReadAllBytesAsync(photoPath, ct);
            var uploaded = await apiClient.UploadPhotoAsync(imageBytes, $"{persona.Id}.png", token, ct);
            
            if (uploaded)
            {
                state.PhotoUploaded = true;
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("📸 Uploaded profile photo for bot {Id}", persona.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Photo upload failed for bot {Id} — will retry next start", persona.Id);
        }
    }

    private async Task RunSyntheticCycleAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();
        var keycloak = scope.ServiceProvider.GetRequiredService<KeycloakBotProvisioner>();

        var activeBots = await db.BotStates
            .Where(b => b.Status == BotStatus.Active)
            .ToListAsync(ct);

        foreach (var bot in activeBots)
        {
            ct.ThrowIfCancellationRequested();
            
            var persona = _personaEngine.GetPersonaById(bot.PersonaId);
            if (persona == null) continue;

            // Set observer context so all API calls get tagged with this bot
            apiClient.SetBotContext(bot.PersonaId, bot.KeycloakUserId ?? "unknown");

            // Check active hours
            var currentHour = DateTime.UtcNow.Hour;
            if (currentHour < persona.Behavior.ActiveStartHourUtc ||
                currentHour >= persona.Behavior.ActiveEndHourUtc)
            {
                bot.Status = BotStatus.Idle;
                await db.SaveChangesAsync(ct);
                continue;
            }

            bot.ResetDailyCountersIfNeeded();

            // Refresh token if needed
            if (bot.TokenExpiresAt <= DateTime.UtcNow && bot.RefreshToken != null)
            {
                try
                {
                    var (newAccess, newRefresh, newExpiry) =
                        await keycloak.RefreshBotTokenAsync(bot.RefreshToken, ct);
                    bot.AccessToken = newAccess;
                    bot.RefreshToken = newRefresh;
                    bot.TokenExpiresAt = newExpiry;
                }
                catch
                {
                    // Full re-auth
                    var (access, refresh, expiry) = await keycloak.GetBotTokenAsync(persona, ct);
                    bot.AccessToken = access;
                    bot.RefreshToken = refresh;
                    bot.TokenExpiresAt = expiry;
                }
            }

            if (bot.AccessToken == null || bot.ProfileId == null) continue;

            try
            {
                // ─── Safety: refresh blocked-by set each cycle ─────
                var blockedIds = await apiClient.GetBlockedByIdsAsync(bot.AccessToken, ct);
                bot.SetBlockedByIds(blockedIds);
                if (blockedIds.Count > 0)
                    _logger.LogDebug("Bot {Id}: blocked by {Count} users", bot.PersonaId, blockedIds.Count);

                // Phase 1: Discover & Swipe
                if (bot.SwipesToday < persona.Behavior.MaxDailySwipes)
                {
                    await DiscoverAndSwipeAsync(bot, persona, apiClient, ct);
                }

                // Phase 2: Chat with matches (with safety guards + LLM)
                if (bot.MessagesSentToday < persona.Behavior.MaxDailyMessages)
                {
                    await ChatWithMatchesAsync(bot, persona, apiClient, ct);
                }

                bot.LastAction = "synthetic_cycle";
                bot.LastActionAt = DateTime.UtcNow;
                bot.Status = BotStatus.Active;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bot {Id} cycle failed", bot.PersonaId);
                bot.LastAction = $"error: {ex.Message[..Math.Min(100, ex.Message.Length)]}";
                bot.Status = BotStatus.Error;
            }

            await db.SaveChangesAsync(ct);

            // Random delay between bots to spread load
            await Task.Delay(TimeSpan.FromSeconds(_random.Next(2, 8)), ct);
        }
    }

    private async Task DiscoverAndSwipeAsync(
        BotState bot, BotPersona persona, DatingAppApiClient apiClient, CancellationToken ct)
    {
        var candidates = await apiClient.GetCandidatesAsync(bot.ProfileId!.Value, bot.AccessToken!, ct);
        if (candidates.Length == 0)
        {
            _logger.LogDebug("Bot {Id}: no candidates available", bot.PersonaId);
            return;
        }

        // Swipe on a random subset
        var maxSwipes = Math.Min(candidates.Length, 5); // Max 5 per cycle
        var shuffled = candidates.OrderBy(_ => _random.Next()).Take(maxSwipes);

        foreach (var candidate in shuffled)
        {
            if (bot.SwipesToday >= persona.Behavior.MaxDailySwipes) break;

            var targetId = candidate.TryGetProperty("id", out var idProp)
                ? idProp.GetInt32()
                : candidate.TryGetProperty("userId", out var uidProp)
                    ? uidProp.GetInt32()
                    : 0;

            if (targetId == 0) continue;

            var isLike = _random.NextDouble() < persona.Behavior.SwipeRightProbability;
            var (success, isMutual) = await apiClient.SwipeAsync(
                bot.ProfileId.Value, targetId, isLike, bot.AccessToken!, ct);

            if (success)
            {
                bot.SwipesToday++;
                if (isMutual) bot.MatchCount++;
                
                _logger.LogInformation("Bot {BotId}: swiped {Direction} on {Target} {Match}",
                    bot.PersonaId, isLike ? "RIGHT" : "LEFT", targetId,
                    isMutual ? "→ MATCH! 🎉" : "");
            }

            // Small delay between swipes (1-3s)
            await Task.Delay(TimeSpan.FromSeconds(_random.Next(1, 4)), ct);
        }
    }

    private async Task ChatWithMatchesAsync(
        BotState bot, BotPersona persona, DatingAppApiClient apiClient, CancellationToken ct)
    {
        if (persona.Behavior.Chattiness == "low" && _random.NextDouble() > 0.3) return;
        if (persona.Behavior.Chattiness == "medium" && _random.NextDouble() > 0.6) return;

        var matches = await apiClient.GetMatchesAsync(bot.ProfileId!.Value, bot.AccessToken!, ct);
        if (matches.Length == 0) return;

        // Pick a random match to message
        var match = matches[_random.Next(matches.Length)];
        
        // Try to get the other user's Keycloak ID
        var otherUserId = match.TryGetProperty("keycloakUserId", out var kcIdProp)
            ? kcIdProp.GetString()
            : match.TryGetProperty("matchedUserId", out var muProp)
                ? muProp.GetString()
                : null;

        if (string.IsNullOrEmpty(otherUserId)) return;

        // ─── Safety guard: skip if user blocked us ─────────────
        if (bot.IsBlockedBy(otherUserId))
        {
            _logger.LogDebug("Bot {BotId}: skipping {Target} — user has blocked us",
                bot.PersonaId, otherUserId);
            return;
        }

        // ─── Conversation guard: skip unresponsive users ───────
        if (bot.IsUnresponsive(otherUserId))
        {
            _logger.LogDebug("Bot {BotId}: skipping {Target} — marked unresponsive (48h cooldown)",
                bot.PersonaId, otherUserId);
            return;
        }

        // ─── Conversation guard: cap per-user messages ─────────
        var sentCount = bot.GetMessageCountForUser(otherUserId);
        if (sentCount >= MaxUnansweredMessages)
        {
            bot.MarkUnresponsive(otherUserId);
            _logger.LogInformation("Bot {BotId}: marking {Target} unresponsive after {Count} unanswered messages",
                bot.PersonaId, otherUserId, sentCount);
            return;
        }

        // ─── Generate message via Conversation Engine (LLM/hybrid/canned) ─────
        string message;
        try
        {
            // Fetch recent message history for LLM context
            var recentMessages = await apiClient.GetConversationMessagesAsync(
                otherUserId, bot.AccessToken!, ct);

            var context = new ConversationContext
            {
                Persona = persona,
                BotUserId = bot.KeycloakUserId ?? "",
                MatchedUserId = otherUserId,
                MessageCount = bot.ConversationCount,
                RecentMessages = recentMessages
            };

            var reply = await _conversationEngine.GenerateReplyAsync(context, ct);
            message = reply.Message;

            _logger.LogDebug("Bot {BotId}: generated {Source} message ({Provider}, {Tokens} tokens, {Latency}ms)",
                bot.PersonaId, reply.Source, reply.Provider ?? "n/a", reply.TokensUsed, reply.LatencyMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bot {BotId}: conversation engine failed, falling back to canned", bot.PersonaId);
            message = _messageProvider.GetMessageForDepth(bot.ConversationCount);
        }
        
        var sent = await apiClient.SendMessageAsync(otherUserId, message, bot.AccessToken!, ct);
        if (sent)
        {
            bot.MessagesSentToday++;
            bot.ConversationCount++;
            bot.IncrementMessageCount(otherUserId);
            _logger.LogInformation("Bot {BotId}: sent message to {Target}: \"{Msg}\" (#{Count} to this user)",
                bot.PersonaId, otherUserId, message[..Math.Min(40, message.Length)],
                bot.GetMessageCountForUser(otherUserId));
        }
    }
}
