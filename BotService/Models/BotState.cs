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
