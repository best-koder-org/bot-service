using System.Diagnostics;
using System.Text.Json;
using BotService.Data;
using BotService.Models;
using Microsoft.EntityFrameworkCore;

namespace BotService.Services.Observer;

/// <summary>
/// Observes bot interactions and records findings (bugs, latency, errors).
/// Wraps API calls to measure and categorize responses.
/// Thread-safe: uses scoped DbContext per recording operation.
/// </summary>
public class BotObserver
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BotObserver> _logger;
    
    /// <summary>Latency threshold in ms before flagging as slow</summary>
    private const long SlowResponseThresholdMs = 3000;
    
    /// <summary>In-memory recent findings for quick API access</summary>
    private readonly List<BotFinding> _recentFindings = new();
    private readonly object _lock = new();
    private const int MaxRecentFindings = 100;

    public BotObserver(IServiceProvider serviceProvider, ILogger<BotObserver> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Record an API interaction result as a potential finding.
    /// Only persists if the result is noteworthy (error, slow, anomaly).
    /// </summary>
    public async Task ObserveApiCall(
        string serviceName,
        string endpoint,
        int httpStatus,
        long responseTimeMs,
        string botPersona,
        string botUserId,
        string? errorDetail = null)
    {
        BotFinding? finding = null;

        if (httpStatus >= 500)
        {
            finding = CreateFinding(FindingType.ServerError, FindingSeverity.High,
                $"Server error {httpStatus} from {serviceName}",
                $"Endpoint {endpoint} returned {httpStatus}. Detail: {errorDetail ?? "none"}",
                serviceName, endpoint, httpStatus, responseTimeMs, botPersona, botUserId);
        }
        else if (httpStatus == 429)
        {
            finding = CreateFinding(FindingType.RateLimited, FindingSeverity.Medium,
                $"Rate limited by {serviceName}",
                $"Endpoint {endpoint} returned 429. Bot may be too aggressive.",
                serviceName, endpoint, httpStatus, responseTimeMs, botPersona, botUserId);
        }
        else if (httpStatus == 401 || httpStatus == 403)
        {
            finding = CreateFinding(FindingType.AuthFailure, FindingSeverity.High,
                $"Auth failure {httpStatus} on {serviceName}",
                $"Endpoint {endpoint} returned {httpStatus}. Token may be expired or invalid.",
                serviceName, endpoint, httpStatus, responseTimeMs, botPersona, botUserId);
        }
        else if (httpStatus >= 400 && httpStatus < 500)
        {
            finding = CreateFinding(FindingType.ClientError, FindingSeverity.Medium,
                $"Client error {httpStatus} from {serviceName}",
                $"Endpoint {endpoint} returned {httpStatus}. Detail: {errorDetail ?? "none"}",
                serviceName, endpoint, httpStatus, responseTimeMs, botPersona, botUserId);
        }
        else if (responseTimeMs > SlowResponseThresholdMs)
        {
            finding = CreateFinding(FindingType.SlowResponse, FindingSeverity.Low,
                $"Slow response from {serviceName} ({responseTimeMs}ms)",
                $"Endpoint {endpoint} took {responseTimeMs}ms (threshold: {SlowResponseThresholdMs}ms)",
                serviceName, endpoint, httpStatus, responseTimeMs, botPersona, botUserId);
        }

        if (finding != null)
        {
            await PersistFinding(finding);
        }
    }

    /// <summary>Record a timeout finding</summary>
    public async Task ObserveTimeout(string serviceName, string endpoint, string botPersona, string botUserId)
    {
        var finding = CreateFinding(FindingType.Timeout, FindingSeverity.High,
            $"Request to {serviceName} timed out",
            $"Endpoint {endpoint} did not respond within timeout period.",
            serviceName, endpoint, null, null, botPersona, botUserId);
        
        await PersistFinding(finding);
    }

    /// <summary>Record a state inconsistency</summary>
    public async Task ObserveStateInconsistency(string description, string serviceName, string botPersona, string botUserId)
    {
        var finding = CreateFinding(FindingType.StateInconsistency, FindingSeverity.Medium,
            $"State inconsistency in {serviceName}",
            description,
            serviceName, "", null, null, botPersona, botUserId);
        
        await PersistFinding(finding);
    }

    /// <summary>Record a custom observation</summary>
    public async Task RecordObservation(FindingType type, FindingSeverity severity,
        string title, string description, string serviceName, string botPersona, string botUserId,
        object? context = null)
    {
        var finding = CreateFinding(type, severity, title, description,
            serviceName, "", null, null, botPersona, botUserId);
        
        if (context != null)
            finding.ContextJson = JsonSerializer.Serialize(context);
        
        await PersistFinding(finding);
    }

    /// <summary>Get recent findings (from memory, fast)</summary>
    public List<BotFinding> GetRecentFindings(int count = 50)
    {
        lock (_lock)
        {
            return _recentFindings.TakeLast(Math.Min(count, MaxRecentFindings)).ToList();
        }
    }

    /// <summary>Get findings summary stats</summary>
    public async Task<FindingsSummary> GetSummaryAsync(DateTime? since = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        
        var query = db.BotFindings.AsQueryable();
        if (since.HasValue)
            query = query.Where(f => f.FoundAt >= since.Value);
        
        var findings = await query.ToListAsync();
        
        return new FindingsSummary
        {
            TotalFindings = findings.Count,
            UnresolvedCount = findings.Count(f => !f.IsResolved),
            BySeverity = findings.GroupBy(f => f.Severity).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            ByType = findings.GroupBy(f => f.Type).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            ByService = findings.GroupBy(f => f.AffectedService).ToDictionary(g => g.Key, g => g.Count()),
            Since = since ?? findings.MinBy(f => f.FoundAt)?.FoundAt ?? DateTime.UtcNow
        };
    }

    private BotFinding CreateFinding(FindingType type, FindingSeverity severity,
        string title, string description, string service, string endpoint,
        int? httpStatus, long? responseTimeMs, string botPersona, string botUserId)
    {
        return new BotFinding
        {
            FoundAt = DateTime.UtcNow,
            Type = type,
            Severity = severity,
            Title = title,
            Description = description,
            AffectedService = service,
            Endpoint = endpoint,
            HttpStatus = httpStatus,
            ResponseTimeMs = responseTimeMs,
            BotPersona = botPersona,
            BotUserId = botUserId
        };
    }

    private async Task PersistFinding(BotFinding finding)
    {
        // In-memory cache
        lock (_lock)
        {
            _recentFindings.Add(finding);
            if (_recentFindings.Count > MaxRecentFindings)
                _recentFindings.RemoveAt(0);
        }

        // Persist to DB
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            db.BotFindings.Add(finding);
            await db.SaveChangesAsync();
            
            _logger.LogWarning("[FINDING] {Severity} {Type}: {Title} (service={Service}, bot={Bot})",
                finding.Severity, finding.Type, finding.Title, finding.AffectedService, finding.BotPersona);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist finding: {Title}", finding.Title);
        }
    }
}

public class FindingsSummary
{
    public int TotalFindings { get; set; }
    public int UnresolvedCount { get; set; }
    public Dictionary<string, int> BySeverity { get; set; } = new();
    public Dictionary<string, int> ByType { get; set; } = new();
    public Dictionary<string, int> ByService { get; set; } = new();
    public DateTime Since { get; set; }
}
