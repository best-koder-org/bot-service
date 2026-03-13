namespace BotService.Models;

/// <summary>
/// A finding recorded by the bot observer during automated interactions.
/// Captures bugs, UX issues, performance anomalies, and behavioral observations.
/// </summary>
public class BotFinding
{
    public int Id { get; set; }
    
    /// <summary>ISO timestamp when the finding was recorded</summary>
    public DateTime FoundAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>Category of the finding</summary>
    public FindingType Type { get; set; }
    
    /// <summary>Impact severity</summary>
    public FindingSeverity Severity { get; set; }
    
    /// <summary>Short title/summary of the finding</summary>
    public string Title { get; set; } = "";
    
    /// <summary>Detailed description with reproduction context</summary>
    public string Description { get; set; } = "";
    
    /// <summary>Which service was involved (e.g. "UserService", "SwipeService")</summary>
    public string AffectedService { get; set; } = "";
    
    /// <summary>The API endpoint or action that triggered the finding</summary>
    public string Endpoint { get; set; } = "";
    
    /// <summary>HTTP status code if applicable</summary>
    public int? HttpStatus { get; set; }
    
    /// <summary>Response time in ms if applicable</summary>
    public long? ResponseTimeMs { get; set; }
    
    /// <summary>Bot persona name that triggered this</summary>
    public string BotPersona { get; set; } = "";
    
    /// <summary>Bot user ID</summary>
    public string BotUserId { get; set; } = "";
    
    /// <summary>Additional context as JSON (request/response snippets, etc.)</summary>
    public string ContextJson { get; set; } = "{}";
    
    /// <summary>Whether this finding has been acknowledged/resolved</summary>
    public bool IsResolved { get; set; }
    
    /// <summary>Resolution notes</summary>
    public string? ResolutionNotes { get; set; }
    
    /// <summary>When the finding was resolved</summary>
    public DateTime? ResolvedAt { get; set; }
}

public enum FindingType
{
    /// <summary>Server returned 5xx error</summary>
    ServerError,
    
    /// <summary>Server returned 4xx (unexpected for valid bot requests)</summary>
    ClientError,
    
    /// <summary>Response exceeded acceptable latency threshold</summary>
    SlowResponse,
    
    /// <summary>Request timed out entirely</summary>
    Timeout,
    
    /// <summary>Unexpected response format or missing data</summary>
    DataAnomaly,
    
    /// <summary>Rate limit hit (429)</summary>
    RateLimited,
    
    /// <summary>Authentication/authorization issue</summary>
    AuthFailure,
    
    /// <summary>Match or conversation state inconsistency</summary>
    StateInconsistency,
    
    /// <summary>Message delivery failure</summary>
    MessageDeliveryFailed,
    
    /// <summary>Photo upload/retrieval issue</summary>
    PhotoIssue,
    
    /// <summary>General UX observation</summary>
    UxObservation,
    
    /// <summary>Performance pattern (not a single slow request but a trend)</summary>
    PerformanceTrend
}

public enum FindingSeverity
{
    /// <summary>Minor observation, informational only</summary>
    Info,
    
    /// <summary>Low impact, works but imperfect</summary>
    Low,
    
    /// <summary>Noticeable issue affecting UX</summary>
    Medium,
    
    /// <summary>Significant issue, core flow impacted</summary>
    High,
    
    /// <summary>Critical failure, blocking core functionality</summary>
    Critical
}
