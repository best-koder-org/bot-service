using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using BotService.Services.Observer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BotService.Tests.Services;

public class BotReporterTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbName;
    private readonly BotReporter _reporter;

    public BotReporterTests()
    {
        _dbName = $"BotReporterTests_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<BotDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddSingleton<BotObserver>();
        services.AddSingleton(new Mock<ILogger<BotObserver>>().Object);
        
        _sp = services.BuildServiceProvider();

        var config = new Mock<IOptionsMonitor<BotServiceOptions>>();
        config.Setup(c => c.CurrentValue).Returns(new BotServiceOptions
        {
            Observer = new ObserverOptions { ReportIntervalHours = 6 }
        });

        _reporter = new BotReporter(
            _sp,
            new Mock<ILogger<BotReporter>>().Object,
            config.Object,
            _sp.GetRequiredService<BotObserver>());
    }

    public void Dispose() => _sp.Dispose();

    private async Task SeedFindingsAsync(params BotFinding[] findings)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        db.BotFindings.AddRange(findings);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GenerateReport_NoFindings_ReturnsZeroCounts()
    {
        var report = await _reporter.GenerateReportAsync();
        
        Assert.Equal(0, report.TotalFindings);
        Assert.Equal(0, report.HighSeverityCount);
        Assert.Equal(0, report.MediumSeverityCount);
        Assert.Equal(0, report.LowSeverityCount);
        Assert.False(report.NeedsAttention);
        Assert.Empty(report.TopIssues);
        Assert.Empty(report.AffectedServices);
        Assert.Empty(report.AffectedBots);
    }

    [Fact]
    public async Task GenerateReport_WithFindings_CorrectCounts()
    {
        await SeedFindingsAsync(
            CreateFinding(FindingType.ServerError, FindingSeverity.High, "UserService"),
            CreateFinding(FindingType.ServerError, FindingSeverity.High, "UserService"),
            CreateFinding(FindingType.SlowResponse, FindingSeverity.Low, "MatchmakingService"),
            CreateFinding(FindingType.RateLimited, FindingSeverity.Medium, "SwipeService")
        );

        var report = await _reporter.GenerateReportAsync();

        Assert.Equal(4, report.TotalFindings);
        Assert.Equal(2, report.HighSeverityCount);
        Assert.Equal(1, report.MediumSeverityCount);
        Assert.Equal(1, report.LowSeverityCount);
        Assert.True(report.NeedsAttention);
    }

    [Fact]
    public async Task GenerateReport_TopIssues_OrderedByCount()
    {
        await SeedFindingsAsync(
            CreateFinding(FindingType.ServerError, FindingSeverity.High, "svc"),
            CreateFinding(FindingType.ServerError, FindingSeverity.High, "svc"),
            CreateFinding(FindingType.ServerError, FindingSeverity.High, "svc"),
            CreateFinding(FindingType.SlowResponse, FindingSeverity.Low, "svc"),
            CreateFinding(FindingType.RateLimited, FindingSeverity.Medium, "svc")
        );

        var report = await _reporter.GenerateReportAsync();

        Assert.Equal("ServerError", report.TopIssues[0].Type);
        Assert.Equal(3, report.TopIssues[0].Count);
    }

    [Fact]
    public async Task GenerateReport_AffectedServices_Grouped()
    {
        await SeedFindingsAsync(
            CreateFinding(FindingType.ServerError, FindingSeverity.High, "UserService"),
            CreateFinding(FindingType.ServerError, FindingSeverity.High, "UserService"),
            CreateFinding(FindingType.SlowResponse, FindingSeverity.Low, "SwipeService")
        );

        var report = await _reporter.GenerateReportAsync();

        Assert.Equal(2, report.AffectedServices["UserService"]);
        Assert.Equal(1, report.AffectedServices["SwipeService"]);
    }

    [Fact]
    public async Task GenerateReport_AffectedBots_Grouped()
    {
        await SeedFindingsAsync(
            CreateFinding(FindingType.ServerError, FindingSeverity.High, "svc", "Anna"),
            CreateFinding(FindingType.ServerError, FindingSeverity.High, "svc", "Anna"),
            CreateFinding(FindingType.SlowResponse, FindingSeverity.Low, "svc", "Erik")
        );

        var report = await _reporter.GenerateReportAsync();

        Assert.Equal(2, report.AffectedBots["Anna"]);
        Assert.Equal(1, report.AffectedBots["Erik"]);
    }

    [Fact]
    public async Task GenerateReport_OnlyIncludesRecentFindings()
    {
        // Old finding (beyond 6h window)
        var oldFinding = CreateFinding(FindingType.ServerError, FindingSeverity.High, "svc");
        oldFinding.FoundAt = DateTime.UtcNow.AddHours(-10);
        
        // Recent finding
        var recentFinding = CreateFinding(FindingType.SlowResponse, FindingSeverity.Low, "svc");
        
        await SeedFindingsAsync(oldFinding, recentFinding);

        var report = await _reporter.GenerateReportAsync();

        Assert.Equal(1, report.TotalFindings);
        Assert.Equal(0, report.HighSeverityCount);
    }

    [Fact]
    public async Task GenerateReport_NeedsAttention_OnlyWhenHighSeverity()
    {
        await SeedFindingsAsync(
            CreateFinding(FindingType.SlowResponse, FindingSeverity.Low, "svc"),
            CreateFinding(FindingType.RateLimited, FindingSeverity.Medium, "svc")
        );

        var report = await _reporter.GenerateReportAsync();
        Assert.False(report.NeedsAttention);
    }

    [Fact]
    public void FindingsReport_HasCorrectTimePeriod()
    {
        var report = new FindingsReport
        {
            GeneratedAt = DateTime.UtcNow,
            PeriodStart = DateTime.UtcNow.AddHours(-6),
            PeriodEnd = DateTime.UtcNow
        };
        Assert.True((report.PeriodEnd - report.PeriodStart).TotalHours >= 5.9);
    }

    [Fact]
    public void TopIssue_HasExpectedProperties()
    {
        var issue = new TopIssue("ServerError", 5, FindingSeverity.High);
        Assert.Equal("ServerError", issue.Type);
        Assert.Equal(5, issue.Count);
        Assert.Equal(FindingSeverity.High, issue.MaxSeverity);
    }

    private static BotFinding CreateFinding(
        FindingType type, FindingSeverity severity, string service, string persona = "TestBot") =>
        new()
        {
            FoundAt = DateTime.UtcNow,
            Type = type,
            Severity = severity,
            Title = $"Test {type}",
            Description = "Test description",
            AffectedService = service,
            BotPersona = persona,
            BotUserId = "test-user-id"
        };
}
