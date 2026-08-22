using System.Collections.Concurrent;
using System.Text.Json;
using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using BotService.Services.Content;
using BotService.Services.Conversation;
using BotService.Services.Observer;
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
    private readonly BotObserver _observer;
    private readonly DemoRuntimeState? _demoState;

    /// <summary>Last time each bot refreshed its blocked-by set (throttled to reduce safety-service load).</summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastBlockedRefresh = new();

    /// <summary>Max unanswered messages to any single user before flagging unresponsive</summary>
    private const int MaxUnansweredMessages = 20;

    public SyntheticUserService(
        IServiceProvider serviceProvider,
        ILogger<SyntheticUserService> logger,
        IOptionsMonitor<BotServiceOptions> config,
        BotPersonaEngine personaEngine,
        MessageContentProvider messageProvider,
        IConversationEngine conversationEngine,
        BotObserver observer,
        DemoRuntimeState? demoState = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
        _personaEngine = personaEngine;
        _messageProvider = messageProvider;
        _conversationEngine = conversationEngine;
        _observer = observer;
        _demoState = demoState;
    }

    /// <summary>Demo mode is active when the runtime toggle says so, else the appsettings value.</summary>
    private bool DemoEnabled => _demoState?.Enabled ?? _config.CurrentValue.Demo.Enabled;

    /// <summary>Reactive-only (like-back) is active per runtime toggle, else appsettings.</summary>
    private bool DemoReactiveOnly => _demoState?.ReactiveOnly ?? _config.CurrentValue.Demo.ReactiveOnly;

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
        // 1. Provision demo-user first (dev sign-in)
        var demoPersonas = _personaEngine.GetPersonasForMode("demo");
        await ProvisionPersonasAsync(demoPersonas, ct);

        // 2. Provision all synthetic bots (they all get profiles so they appear in the
        // discover feed) but cap how many actually RUN so the demo stack stays responsive.
        var syntheticPersonas = _personaEngine.GetPersonasForMode("synthetic");
        await ProvisionPersonasAsync(syntheticPersonas, ct);
        await ApplyActiveBotLimitAsync(ct);

        // Self-healing dev conveniences on restart:
        // (a) fresh daily swipe/message counters, (b) correct bot→profile mappings
        // so messaging match checks never 403 on stale data.
        await ResetDailyCountersOnStartupAsync(ct);
        await SyncBotMappingsOnStartupAsync(ct);

        // 3. Pre-seed mutual likes so demo-user has matches immediately
        await PreSeedMutualLikesAsync(ct);

        if (syntheticPersonas.Count == 0)
        {
            _logger.LogWarning("No synthetic personas loaded — only demo-user provisioned");
        }
    }

    /// <summary>
    /// Cap the number of ACTIVE synthetic bots (Demo.ActiveBotLimit). All personas stay
    /// provisioned (visible in discover) but only the first N run their behavior loop.
    /// Keeps the dev stack responsive and avoids overwhelming safety-service with 429s.
    /// </summary>
    private async Task ApplyActiveBotLimitAsync(CancellationToken ct)
    {
        var limit = _config.CurrentValue.Demo.ActiveBotLimit;
        if (limit <= 0) return;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var activeBots = await db.BotStates
            .Where(b => b.Status == BotStatus.Active && b.PersonaId != "demo-user")
            .OrderBy(b => b.Id)
            .ToListAsync(ct);

        var changed = false;
        for (var i = 0; i < activeBots.Count; i++)
        {
            if (i >= limit)
            {
                activeBots[i].Status = BotStatus.Idle;
                changed = true;
            }
        }
        if (changed)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Active bot limit {Limit} applied: kept {Kept} active, idled the rest",
                limit, Math.Min(activeBots.Count, limit));
        }
    }

    /// <summary>
    /// Zero the daily swipe/message counters for active bots on startup so restarts don't
    /// leave bots blocked by MaxDailySwipes/MaxDailyMessages mid-day. (Demo/dev convenience.)
    /// </summary>
    private async Task ResetDailyCountersOnStartupAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var bots = await db.BotStates
            .Where(b => b.Status == BotStatus.Active)
            .ToListAsync(ct);
        foreach (var b in bots)
        {
            b.SwipesToday = 0;
            b.MessagesSentToday = 0;
            b.CounterResetDate = DateTime.UtcNow.Date;
        }
        if (bots.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Reset daily counters for {Count} active bots on startup", bots.Count);
        }
    }

    /// <summary>
    /// Push the authoritative (keycloakId → profileId) mapping for every bot to swipe-service
    /// so the messaging match check resolves correctly without a manual mapping repair.
    /// </summary>
    private async Task SyncBotMappingsOnStartupAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();
        var keycloak = scope.ServiceProvider.GetRequiredService<KeycloakBotProvisioner>();

        var pairs = await db.BotStates
            .Where(b => b.KeycloakUserId != null && b.KeycloakUserId != "" && b.ProfileId != null)
            .Select(b => new { b.ProfileId, b.KeycloakUserId })
            .ToListAsync(ct);
        var list = pairs
            .Where(p => p.ProfileId.HasValue)
            .Select(p => (p.ProfileId!.Value, p.KeycloakUserId!))
            .ToList();
        if (list.Count == 0) return;

        var demoPersona = _personaEngine.GetPersonaById("demo-user");
        if (demoPersona == null) return;

        var (token, _, _) = await keycloak.GetBotTokenAsync(demoPersona, ct);
        if (string.IsNullOrEmpty(token)) return;

        var ok = await apiClient.SyncBotMappingsAsync(list, token, ct);
        _logger.LogInformation("Synced {Count} bot mappings to swipe-service: {Result}",
            list.Count, ok ? "ok" : "failed");
    }


    /// <summary>Provision a set of personas: create Keycloak user, profile, upload photo.</summary>
    private async Task ProvisionPersonasAsync(List<BotPersona> personas, CancellationToken ct)
    {
        if (personas.Count == 0) return;

        using var scope = _serviceProvider.CreateScope();
        var keycloak = scope.ServiceProvider.GetRequiredService<KeycloakBotProvisioner>();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();

        foreach (var persona in personas)
        {
            try
            {
                var existingState = await db.BotStates
                    .FirstOrDefaultAsync(b => b.PersonaId == persona.Id, ct);

                if (existingState is { Status: BotStatus.Active })
                {
                    if (!existingState.PhotoUploaded)
                    {
                        var (tok, _, _) = await keycloak.GetBotTokenAsync(persona, ct);
                        await UploadBotPhotoAsync(persona, apiClient, tok, existingState, db, ct);
                    }
                    _logger.LogDebug("Persona {Id} already active, skipping provision", persona.Id);
                    continue;
                }

                var keycloakId = await keycloak.EnsureBotUserAsync(persona, ct);
                var (accessToken, refreshToken, expiresAt) = await keycloak.GetBotTokenAsync(persona, ct);

                // Use the JWT-authenticated provision endpoint.
                // Handles create and reconcile in one call — no more email conflicts or stale IDs.
                var (profileId, created) = await apiClient.ProvisionBotAsync(accessToken, persona, ct);

                if (profileId == null)
                {
                    _logger.LogError("Failed to provision profile for persona {PersonaId}", persona.Id);
                    continue;
                }

                if (created)
                {
                    _logger.LogInformation("Created new profile {ProfileId} for persona {PersonaId}", profileId, persona.Id);
                }
                else
                {
                    _logger.LogInformation("Reconciled profile {ProfileId} for persona {PersonaId}", profileId, persona.Id);
                }

                if (existingState != null)
                {
                    existingState.KeycloakUserId = keycloakId;
                    existingState.ProfileId = profileId;
                    existingState.AccessToken = accessToken;
                    existingState.RefreshToken = refreshToken;
                    existingState.TokenExpiresAt = expiresAt;
                    if (existingState.Status != BotStatus.Paused)
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
                _logger.LogInformation("Provisioned persona: {Id} (profile={ProfileId})",
                    persona.Id, profileId);

                var state = existingState ?? await db.BotStates
                    .FirstOrDefaultAsync(b => b.PersonaId == persona.Id, ct);
                if (state != null && !state.PhotoUploaded)
                {
                    await UploadBotPhotoAsync(persona, apiClient, accessToken, state, db, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to provision persona {Id}", persona.Id);
            }
        }
    }

    /// <summary>
    /// Pre-seed mutual likes so demo-user has matches immediately on first sign-in.
    /// 3-4 female bots swipe right on demo-user, then demo-user swipes right on them → instant matches.
    /// </summary>
    private async Task PreSeedMutualLikesAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();
        var keycloak = scope.ServiceProvider.GetRequiredService<KeycloakBotProvisioner>();

        var demoState = await db.BotStates.FirstOrDefaultAsync(b => b.PersonaId == "demo-user", ct);
        if (demoState?.ProfileId == null)
        {
            _logger.LogWarning("demo-user not provisioned, skipping pre-seed");
            return;
        }

        // Pick bots to pre-like the demo user (config-driven).
        var demo = _config.CurrentValue.Demo;
        var preSeedIds = (demo.PreSeedBotIds ?? new List<string>())
            .Take(Math.Max(0, demo.PreSeedBotCount))
            .ToList();
        if (preSeedIds.Count == 0) return;

        var matchCount = 0;

        foreach (var botId in preSeedIds)
        {
            var botState = await db.BotStates.FirstOrDefaultAsync(b => b.PersonaId == botId, ct);
            if (botState?.ProfileId == null || botState.AccessToken == null) continue;

            var persona = _personaEngine.GetPersonaById(botId);
            if (persona == null) continue;

            try
            {
                // Refresh bot token if expired
                if (botState.TokenExpiresAt <= DateTime.UtcNow)
                {
                    var (access, refresh, expiry) = await keycloak.GetBotTokenAsync(persona, ct);
                    botState.AccessToken = access;
                    botState.RefreshToken = refresh;
                    botState.TokenExpiresAt = expiry;
                    await db.SaveChangesAsync(ct);
                }

                // Bot swipes right on demo-user
                var (s1, _) = await apiClient.SwipeAsync(
                    botState.ProfileId.Value, demoState.ProfileId.Value, true, botState.AccessToken, ct);

                if (!s1)
                {
                    _logger.LogWarning("Pre-seed: {Bot} failed to swipe on demo-user", botId);
                    continue;
                }

                // Refresh demo-user token if expired
                var demoPersona = _personaEngine.GetPersonaById("demo-user");
                if (demoState.TokenExpiresAt <= DateTime.UtcNow && demoPersona != null)
                {
                    var (access, refresh, expiry) = await keycloak.GetBotTokenAsync(demoPersona, ct);
                    demoState.AccessToken = access;
                    demoState.RefreshToken = refresh;
                    demoState.TokenExpiresAt = expiry;
                    await db.SaveChangesAsync(ct);
                }

                // Demo-user swipes right on bot → mutual match (only when auto-reciprocate is enabled).
                // When disabled, the human tester swipes on the bot themselves and matches instantly.
                if (demo.PreSeedAutoReciprocate && demoState.AccessToken != null)
                {
                    var (s2, isMutual) = await apiClient.SwipeAsync(
                        demoState.ProfileId.Value, botState.ProfileId.Value, true, demoState.AccessToken, ct);

                    if (s2 && isMutual)
                    {
                        matchCount++;
                        _logger.LogInformation("💕 Pre-seeded mutual match: demo-user ↔ {Bot}", botId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pre-seed failed for {Bot}", botId);
            }
        }

        _logger.LogInformation("Pre-seed complete: {Count} mutual matches for demo-user", matchCount);
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
            var photoId = await apiClient.UploadPhotoAsync(imageBytes, $"{persona.Id}.png", token, ct);
            
            if (photoId != null)
            {
                state.PhotoUploaded = true;
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("📸 Uploaded profile photo for bot {Id} (photoId={PhotoId})", persona.Id, photoId);
                
                // Update UserService profile with photo URL so enrichment finds it
                if (photoId > 0 && state.ProfileId.HasValue)
                {
                    var photoUrl = $"/api/photos/{photoId}/image";
                    await apiClient.UpdateProfileAsync(state.ProfileId.Value, 
                        new { primaryPhotoUrl = photoUrl }, token, ct);
                    _logger.LogInformation("Updated profile {ProfileId} with photo URL {Url}", state.ProfileId.Value, photoUrl);
                }
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
                // ─── Safety: refresh blocked-by set (throttled to once per 60s per bot) ─────
                var now = DateTime.UtcNow;
                if (!_lastBlockedRefresh.TryGetValue(bot.PersonaId, out var lastRefresh) ||
                    (now - lastRefresh).TotalSeconds >= 60)
                {
                    var blockedIds = await apiClient.GetBlockedByIdsAsync(bot.AccessToken, ct);
                    bot.SetBlockedByIds(blockedIds);
                    _lastBlockedRefresh[bot.PersonaId] = now;
                    if (blockedIds.Count > 0)
                        _logger.LogDebug("Bot {Id}: blocked by {Count} users", bot.PersonaId, blockedIds.Count);
                }

                // Phase 1: Discover & Swipe.
                // In Demo ReactiveOnly mode bots do NOT proactively swipe random users — they
                // only reciprocate incoming human likes (like-back), so matches are always
                // human-initiated. Proactive discovery runs only when demo mode is off.
                if (DemoEnabled && DemoReactiveOnly)
                {
                    await ReciprocateIncomingLikesAsync(bot, persona, apiClient, ct);
                }
                else if (bot.SwipesToday < persona.Behavior.MaxDailySwipes && _random.Next(30) == 0)
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
            await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(50, 150)), ct); // fast reply mode
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

    /// <summary>
    /// Reactive "like-back": reciprocate incoming likes from human users so a match only forms
    /// when the human swiped first. Bounded per cycle to keep DB volume low (MaxLikeBackPerCycle).
    /// Like-backs are human-initiated reactions, so they are NOT subject to the proactive
    /// daily-swipe cap (MaxDailySwipes) — that cap only limits random discover swiping.
    /// </summary>
    private async Task ReciprocateIncomingLikesAsync(
        BotState bot, BotPersona persona, DatingAppApiClient apiClient, CancellationToken ct)
    {
        if (bot.AccessToken == null || bot.ProfileId == null) return;

        var likes = await apiClient.GetLikesReceivedAsync(bot.ProfileId.Value, bot.AccessToken, ct);
        if (likes.Length == 0) return;

        var allBotKeycloakIds = await GetAllBotKeycloakIdsAsync();
        var max = Math.Min(likes.Length, Math.Max(1, _config.CurrentValue.Demo.MaxLikeBackPerCycle));
        var likeBacks = 0;

        foreach (var like in likes.Take(max))
        {
            ct.ThrowIfCancellationRequested();

            if (!like.TryGetProperty("userId", out var userIdProp) ||
                !userIdProp.TryGetInt32(out var likerProfileId) || likerProfileId <= 0)
            {
                continue;
            }

            // Resolve liker to Keycloak ID so we can skip other bots + blocked/unresponsive users.
            var likerKeycloakId = await apiClient.GetKeycloakIdForProfileAsync(
                likerProfileId, bot.AccessToken, ct);
            if (string.IsNullOrEmpty(likerKeycloakId)) continue;

            if (allBotKeycloakIds.Contains(likerKeycloakId)) continue; // never like another bot
            if (bot.IsBlockedBy(likerKeycloakId)) continue;            // user blocked us
            if (bot.IsUnresponsive(likerKeycloakId)) continue;         // user ignored us before

            var (success, isMutual) = await apiClient.SwipeAsync(
                bot.ProfileId.Value, likerProfileId, true, bot.AccessToken, ct);

            if (success)
            {
                bot.SwipesToday++;
                if (isMutual) bot.MatchCount++;
                likeBacks++;
                _logger.LogInformation("Bot {BotId}: liked back {Target} {Match}",
                    bot.PersonaId, likerProfileId, isMutual ? "→ MATCH! 🎉" : "");
            }

            // Small delay to spread load
            await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(500, 1500)), ct);
        }

        if (likeBacks > 0)
            _logger.LogDebug("Bot {BotId}: liked back {Count} human(s) this cycle", bot.PersonaId, likeBacks);
    }

    private async Task ChatWithMatchesAsync(
        BotState bot, BotPersona persona, DatingAppApiClient apiClient, CancellationToken ct)
    {
        if (persona.Behavior.Chattiness == "low" && _random.NextDouble() > 0.3) return;
        if (persona.Behavior.Chattiness == "medium" && _random.NextDouble() > 0.6) return;

        var matches = await apiClient.GetMatchesAsync(bot.ProfileId!.Value, bot.AccessToken!, ct);
        if (matches.Length == 0) return;

        // Check ALL matches each cycle for fast reply
        foreach (var match in matches)
        {
        // Try to get the other user's Keycloak ID
        string? otherUserId = null;
        
        // First: check if match response already has a keycloak UUID
        if (match.TryGetProperty("keycloakUserId", out var kcIdProp))
            otherUserId = kcIdProp.GetString();
        
        // Second: resolve matchedUserId (integer profile ID) → Keycloak UUID via UserService
        if (string.IsNullOrEmpty(otherUserId) && match.TryGetProperty("matchedUserId", out var muProp))
        {
            int matchedProfileId = muProp.ValueKind == JsonValueKind.Number
                ? muProp.GetInt32()
                : int.TryParse(muProp.GetString(), out var parsed) ? parsed : 0;
            
            if (matchedProfileId > 0)
            {
                otherUserId = await apiClient.GetKeycloakIdForProfileAsync(
                    matchedProfileId, bot.AccessToken!, ct);
                if (string.IsNullOrEmpty(otherUserId))
                    _logger.LogWarning("Bot {BotId}: could not resolve keycloakId for profile {ProfileId}",
                        bot.PersonaId, matchedProfileId);
            }
        }

        if (string.IsNullOrEmpty(otherUserId)) continue;

        // ─── Bot guard: skip if recipient is another bot ──────
        var allBotKeycloakIds = await GetAllBotKeycloakIdsAsync();
        if (allBotKeycloakIds.Contains(otherUserId))
        {
            _logger.LogDebug("Bot {BotId}: skipping {Target} — recipient is another bot",
                bot.PersonaId, otherUserId);
            continue;
        }

        // ─── Safety guard: skip if user blocked us ─────────────
        if (bot.IsBlockedBy(otherUserId))
        {
            _logger.LogDebug("Bot {BotId}: skipping {Target} — user has blocked us",
                bot.PersonaId, otherUserId);
            continue;
        }

        // ─── Conversation guard: skip unresponsive users ───────
        if (bot.IsUnresponsive(otherUserId))
        {
            _logger.LogDebug("Bot {BotId}: skipping {Target} — marked unresponsive (48h cooldown)",
                bot.PersonaId, otherUserId);
            continue;
        }

        // ─── Conversation guard: cap per-user messages ─────────
        var sentCount = bot.GetMessageCountForUser(otherUserId);
        if (sentCount >= MaxUnansweredMessages)
        {
            bot.MarkUnresponsive(otherUserId);
            _logger.LogInformation("Bot {BotId}: marking {Target} unresponsive after {Count} unanswered messages",
                bot.PersonaId, otherUserId, sentCount);
            continue;
        }

        // ─── Generate message via Conversation Engine (LLM/hybrid/canned) ─────
        string message;
        try
        {
            // Fetch recent message history for LLM context
            var recentMessages = await apiClient.GetConversationMessagesAsync(
                otherUserId, bot.AccessToken!, ct);

            // ─── Opener gate (Demo mode): only send the FIRST message when OpenerOnMatch
            // is enabled. This keeps bots purely reactive unless the demo explicitly allows openers.
            var demo = _config.CurrentValue.Demo;
            if (recentMessages.Count == 0 && DemoEnabled && !demo.OpenerOnMatch)
            {
                _logger.LogDebug("Bot {BotId}: opener disabled in demo mode, waiting for {Target}",
                    bot.PersonaId, otherUserId);
                continue;
            }

            // ─── Turn-taking: only reply if user sent the last message ─────
            // If conversation exists and bot sent the last message, wait for user reply
            if (recentMessages.Count > 0)
            {
                var lastMsg = recentMessages[0]; // newest message (API returns newest-first)
                if (lastMsg.SenderUserId == (bot.KeycloakUserId ?? ""))
                {
                    _logger.LogDebug("Bot {BotId}: waiting for {Target} to reply (last msg was ours)",
                        bot.PersonaId, otherUserId);
                    continue;
                }
            }

            // ─── Message Classification (T323/T324): classify received messages ─────
            if (recentMessages.Count > 0)
            {
                var lastReceived = recentMessages.FirstOrDefault(m => m.SenderUserId != (bot.KeycloakUserId ?? ""));
                if (lastReceived != null && !string.IsNullOrEmpty(lastReceived.Content))
                {
                    var tone = MessageClassifier.Classify(lastReceived.Content);
                    if (MessageClassifier.IsSafetyRelevant(tone))
                    {
                        await _observer.RecordObservation(
                            FindingType.SafetyIncident, FindingSeverity.High,
                            $"Safety-relevant message detected: {tone}",
                            $"Received {tone} message from {otherUserId}: \"{lastReceived.Content[..Math.Min(50, lastReceived.Content.Length)]}...\"",
                            "messaging-service", persona.FirstName, bot.KeycloakUserId ?? "");
                        _logger.LogWarning("Bot {BotId}: received {Tone} message from {User}",
                            bot.PersonaId, tone, otherUserId);
                    }
                }
            }

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

            // ─── Conversation Metrics (T325): track stage ─────
            var msgContents = recentMessages.Select(m => m.Content).ToList();
            var stageResult = ConversationStageDetector.Detect(bot.ConversationCount, msgContents);
            if (stageResult.Reason != "message_count")
            {
                await _observer.RecordObservation(
                    FindingType.ConversationMetric, FindingSeverity.Info,
                    $"Stage accelerated: {stageResult.Stage} ({stageResult.Reason})",
                    $"Conversation with {otherUserId} reached {stageResult.Stage} via {stageResult.Reason} at message #{bot.ConversationCount}",
                    "bot-service", persona.FirstName, bot.KeycloakUserId ?? "");
            }

            _logger.LogDebug("Bot {BotId}: generated {Source} message ({Provider}, {Tokens} tokens, {Latency}ms)",
                bot.PersonaId, reply.Source, reply.Provider ?? "n/a", reply.TokensUsed, reply.LatencyMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bot {BotId}: conversation engine failed, falling back to canned", bot.PersonaId);
            message = _messageProvider.GetMessageForDepth(bot.ConversationCount);
        }
        
        var sent = await apiClient.SendMessageAsync(otherUserId, message, bot.AccessToken!, ct,
            bot.ProfileId?.ToString());
        if (sent)
        {
            bot.MessagesSentToday++;
            bot.ConversationCount++;
            bot.IncrementMessageCount(otherUserId);
            _logger.LogInformation("Bot {BotId}: sent message to {Target}: \"{Msg}\" (#{Count} to this user)",
                bot.PersonaId, otherUserId, message[..Math.Min(40, message.Length)],
                bot.GetMessageCountForUser(otherUserId));
        }
        } // end foreach match
    }

    /// <summary>
    /// Returns true when a BotState represents "another bot" that the service
    /// should NOT message. The demo-user persona is treated as the human test
    /// account, so it is excluded from the skip set (bots may reply to it).
    /// </summary>
    internal static bool IsBotTargetExcluded(BotState bs)
        => bs.KeycloakUserId != null && bs.PersonaId != "demo-user";

    /// <summary>
    /// Returns a set of all bot Keycloak user IDs to prevent bots from messaging each other.
    /// Cached per call; called once per ChatWithMatchesAsync cycle.
    /// </summary>
    private async Task<HashSet<string>> GetAllBotKeycloakIdsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ids = await db.BotStates
            .Where(bs => bs.KeycloakUserId != null && bs.PersonaId != "demo-user")
            .Select(bs => bs.KeycloakUserId!)
            .ToListAsync();
        return new HashSet<string>(ids);
    }
}
