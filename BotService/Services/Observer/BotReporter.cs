using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BotService.Services.Observer;

/// <summary>
/// Background service that periodically generates findings digests.
/// Runs every ReportIntervalHours (default 6), queries recent BotFindings,
/// and logs a structured summary. Future: can push to Slack/email/dashboard.
/// </summary>
public class BotReporter : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BotReporter> _logger;
    private readonly IOptionsMonitor<BotServiceOptions> _config;
    private readonly BotObserver _observer;

    public BotReporter(
        IServiceProvider serviceProvider,
        ILogger<BotReporter> logger,
        IOptionsMonitor<BotServiceOptions> config,
        BotObserver observer)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
        _observer = observer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BotReporter starting — will produce periodic finding digests");
        
        // Wait a bit before first report to let system stabilize
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var report = await GenerateReportAsync(stoppingToken);
                LogReport(report);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BotReporter cycle failed");
            }

            var intervalHours = _config.CurrentValue.Observer?.ReportIntervalHours ?? 6;
            await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
        }

        _logger.LogInformation("BotReporter stopped");
    }

    /// <summary>Generate a findings report for the last reporting interval</summary>
    public async Task<FindingsReport> GenerateReportAsync(CancellationToken ct = default)
    {
        var intervalHours = _config.CurrentValue.Observer?.ReportIntervalHours ?? 6;
        var since = DateTime.UtcNow.AddHours(-intervalHours);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var findings = await db.BotFindings
            .Where(f => f.FoundAt >= since)
            .OrderByDescending(f => f.FoundAt)
            .ToListAsync(ct);

        var highSeverity = findings.Where(f => f.Severity == FindingSeverity.High).ToList();
        var mediumSeverity = findings.Where(f => f.Severity == FindingSeverity.Medium).ToList();
        var lowSeverity = findings.Where(f => f.Severity == FindingSeverity.Low).ToList();

        var topIssues = findings
            .GroupBy(f => f.Type)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => new TopIssue(g.Key.ToString(), g.Count(), g.Max(f => f.Severity)))
            .ToList();

        var affectedServices = findings
            .GroupBy(f => f.AffectedService)
            .ToDictionary(g => g.Key, g => g.Count());

        var affectedBots = findings
            .Where(f => !string.IsNullOrEmpty(f.BotPersona))
            .GroupBy(f => f.BotPersona)
            .ToDictionary(g => g.Key, g => g.Count());

        return new FindingsReport
        {
            GeneratedAt = DateTime.UtcNow,
            PeriodStart = since,
            PeriodEnd = DateTime.UtcNow,
            TotalFindings = findings.Count,
            HighSeverityCount = highSeverity.Count,
            MediumSeverityCount = mediumSeverity.Count,
            LowSeverityCount = lowSeverity.Count,
            TopIssues = topIssues,
            AffectedServices = affectedServices,
            AffectedBots = affectedBots,
            NeedsAttention = highSeverity.Count > 0
        };
    }

    private void LogReport(FindingsReport report)
    {
        if (report.TotalFindings == 0)
        {
            _logger.LogInformation("[BOT-REPORT] No findings in last {Hours}h — all clear ✓",
                (report.PeriodEnd - report.PeriodStart).TotalHours);
            return;
        }

        _logger.LogWarning(
            "[BOT-REPORT] {Total} findings ({High} high, {Medium} medium, {Low} low) in {Hours:F1}h. " +
            "Top: {TopIssues}. Services: {Services}. Attention: {Attention}",
            report.TotalFindings,
            report.HighSeverityCount,
            report.MediumSeverityCount,
            report.LowSeverityCount,
            (report.PeriodEnd - report.PeriodStart).TotalHours,
            string.Join(", ", report.TopIssues.Select(t => $"{t.Type}({t.Count})")),
            string.Join(", ", report.AffectedServices.Select(kv => $"{kv.Key}({kv.Value})")),
            report.NeedsAttention ? "YES" : "no");
    }
}

/// <summary>Structured findings report for a time period</summary>
public class FindingsReport
{
    public DateTime GeneratedAt { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int TotalFindings { get; set; }
    public int HighSeverityCount { get; set; }
    public int MediumSeverityCount { get; set; }
    public int LowSeverityCount { get; set; }
    public List<TopIssue> TopIssues { get; set; } = new();
    public Dictionary<string, int> AffectedServices { get; set; } = new();
    public Dictionary<string, int> AffectedBots { get; set; } = new();
    public bool NeedsAttention { get; set; }
}

public record TopIssue(string Type, int Count, FindingSeverity MaxSeverity);
