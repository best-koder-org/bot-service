using System.Text.Json;

namespace BotService.Models;

/// <summary>
/// Persisted state for a running bot — tracks Keycloak IDs, tokens, action counts.
/// Stored in SQLite so bots can resume across restarts.
/// </summary>
public class BotState
{
    public int Id { get; set; }
    public string PersonaId { get; set; } = string.Empty;
    public string KeycloakUserId { get; set; } = string.Empty;
    public int? ProfileId { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? TokenExpiresAt { get; set; }
    
    // Counters (reset daily)
    public int SwipesToday { get; set; }
    public int MessagesSentToday { get; set; }
    public DateTime CounterResetDate { get; set; } = DateTime.UtcNow.Date;
    
    // State tracking
    public BotStatus Status { get; set; } = BotStatus.Provisioning;
    public string? LastAction { get; set; }
    public DateTime? LastActionAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Relationships
    public int MatchCount { get; set; }
    public int ConversationCount { get; set; }
    
    /// <summary>Whether profile photo has been uploaded (only happens once)</summary>
    public bool PhotoUploaded { get; set; }
    
    // ─── Conversation guards ────────────────────────────────────
    // Tracks per-user message counts to prevent spam / harassment
    // Serialized as JSON: { "keycloakId": messageCount, ... }
    
    /// <summary>JSON blob tracking messages sent per target user</summary>
    public string MessagesSentPerUserJson { get; set; } = "{}";
    
    /// <summary>JSON blob tracking users who never responded (keycloakId → last message timestamp)</summary>
    public string UnresponsiveUsersJson { get; set; } = "{}";
    
    /// <summary>Set of blocked user IDs, refreshed each cycle</summary>
    public string BlockedByIdsJson { get; set; } = "[]";
    
    /// <summary>Reset daily counters if it's a new day</summary>
    public void ResetDailyCountersIfNeeded()
    {
        var today = DateTime.UtcNow.Date;
        if (CounterResetDate < today)
        {
            SwipesToday = 0;
            MessagesSentToday = 0;
            CounterResetDate = today;
        }
    }

    // ─── Conversation tracking helpers ──────────────────────────

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    /// <summary>How many messages has this bot sent to a specific user?</summary>
    public int GetMessageCountForUser(string keycloakId)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(MessagesSentPerUserJson, _jsonOpts);
            return dict != null && dict.TryGetValue(keycloakId, out var count) ? count : 0;
        }
        catch { return 0; }
    }
    
    /// <summary>Record that we sent a message to this user</summary>
    public void IncrementMessageCount(string keycloakId)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(MessagesSentPerUserJson, _jsonOpts) 
                       ?? new Dictionary<string, int>();
            dict[keycloakId] = dict.GetValueOrDefault(keycloakId, 0) + 1;
            MessagesSentPerUserJson = JsonSerializer.Serialize(dict, _jsonOpts);
        }
        catch { /* swallow — non-critical tracking */ }
    }

    /// <summary>Mark a user as unresponsive (no reply in a while)</summary>
    public void MarkUnresponsive(string keycloakId)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(UnresponsiveUsersJson, _jsonOpts) 
                       ?? new Dictionary<string, DateTime>();
            dict[keycloakId] = DateTime.UtcNow;
            UnresponsiveUsersJson = JsonSerializer.Serialize(dict, _jsonOpts);
        }
        catch { }
    }
    
    /// <summary>Is this user marked as unresponsive?</summary>
    public bool IsUnresponsive(string keycloakId)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(UnresponsiveUsersJson, _jsonOpts);
            if (dict == null || !dict.TryGetValue(keycloakId, out var markedAt)) return false;
            // Cool down: retry after 48 hours
            return (DateTime.UtcNow - markedAt).TotalHours < 48;
        }
        catch { return false; }
    }

    /// <summary>Update the blocked-by user ID set</summary>
    public void SetBlockedByIds(HashSet<string> ids)
    {
        try { BlockedByIdsJson = JsonSerializer.Serialize(ids, _jsonOpts); }
        catch { }
    }
    
    /// <summary>Is this user in our blocked-by list?</summary>
    public bool IsBlockedBy(string keycloakId)
    {
        try
        {
            var set = JsonSerializer.Deserialize<HashSet<string>>(BlockedByIdsJson, _jsonOpts);
            return set != null && set.Contains(keycloakId);
        }
        catch { return false; }
    }
}

public enum BotStatus
{
    Provisioning,
    Active,
    Idle,
    Paused,
    Error,
    Decommissioned
}
