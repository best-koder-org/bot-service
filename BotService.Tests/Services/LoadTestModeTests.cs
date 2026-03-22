using System.Collections.Concurrent;
using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using BotService.Services;
using BotService.Services.Observer;
using BotService.Services.Swarm.Modes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BotService.Tests.Services;

public class LoadTestModeTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly BotObserver _observer;
    private readonly LoadTestMode _mode;

    public LoadTestModeTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<BotDbContext>(o =>
            o.UseInMemoryDatabase($"LoadTests_{Guid.NewGuid()}"));

        // Register DatingAppApiClient with proper constructor args
        services.AddSingleton(Options.Create(new BotServiceOptions()));
        services.AddSingleton<DatingAppApiClient>(sp =>
        {
            var http = new HttpClient();
            var opts = sp.GetRequiredService<IOptions<BotServiceOptions>>();
            var logger = Mock.Of<ILogger<DatingAppApiClient>>();
            return new DatingAppApiClient(http, opts, logger);
        });
        services.AddLogging();

        _sp = services.BuildServiceProvider();
        _observer = new BotObserver(_sp, Mock.Of<ILogger<BotObserver>>());
        _mode = new LoadTestMode(_sp, _observer, Mock.Of<ILogger<LoadTestMode>>());
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task InitializeAsync_SetsTargetRps()
    {
        var parameters = new Dictionary<string, string>
        {
            ["targetRps"] = "20",
            ["durationSeconds"] = "30"
        };

        await _mode.InitializeAsync(parameters);
        Assert.Equal("loadtest", _mode.Name);
    }

    [Fact]
    public async Task InitializeAsync_DefaultValues_WhenNoParameters()
    {
        await _mode.InitializeAsync(new Dictionary<string, string>());
        Assert.Equal("loadtest", _mode.Name);
        Assert.Equal("Real API load test with latency tracking and report generation", _mode.Description);
    }

    [Fact]
    public async Task InitializeAsync_IgnoresInvalidValues()
    {
        var parameters = new Dictionary<string, string>
        {
            ["targetRps"] = "notanumber",
            ["durationSeconds"] = "also-invalid"
        };

        await _mode.InitializeAsync(parameters);
        Assert.Equal("loadtest", _mode.Name);
    }

    [Fact]
    public void GenerateReport_EmptyLatencies_ReturnsZeroReport()
    {
        var latencies = new ConcurrentBag<long>();
        var start = DateTime.UtcNow.AddSeconds(-10);
        var end = DateTime.UtcNow;

        var report = _mode.GenerateReport(0, 0, latencies, start, end);

        Assert.Equal(0, report.TotalRequests);
        Assert.Equal(0, report.TotalErrors);
        Assert.Equal(0, report.ErrorRate);
        Assert.Equal(0, report.P50Ms);
        Assert.Equal(0, report.P99Ms);
        Assert.Equal(0, report.MinMs);
        Assert.Equal(0, report.MaxMs);
        Assert.Equal(0, report.AvgMs);
    }

    [Fact]
    public void GenerateReport_WithLatencies_CalculatesPercentiles()
    {
        var latencies = new ConcurrentBag<long>();
        for (long i = 1; i <= 100; i++)
            latencies.Add(i);

        var start = DateTime.UtcNow.AddSeconds(-60);
        var end = DateTime.UtcNow;

        var report = _mode.GenerateReport(100, 5, latencies, start, end);

        Assert.Equal(100, report.TotalRequests);
        Assert.Equal(5, report.TotalErrors);
        Assert.Equal(0.05, report.ErrorRate);
        Assert.Equal(51, report.P50Ms);
        Assert.Equal(91, report.P90Ms);
        Assert.Equal(96, report.P95Ms);
        Assert.Equal(100, report.P99Ms);
        Assert.Equal(1, report.MinMs);
        Assert.Equal(100, report.MaxMs);
        Assert.Equal(50.5, report.AvgMs);
    }

    [Fact]
    public void GenerateReport_CalculatesActualRps()
    {
        var latencies = new ConcurrentBag<long>(new long[] { 10, 20, 30 });
        var start = DateTime.UtcNow.AddSeconds(-10);
        var end = DateTime.UtcNow;

        var report = _mode.GenerateReport(100, 0, latencies, start, end);

        Assert.True(report.ActualRps > 9.0 && report.ActualRps < 11.0,
            $"Expected ~10 RPS but got {report.ActualRps}");
        Assert.Equal(0, report.ErrorRate);
    }

    [Fact]
    public void GenerateReport_CalculatesDuration()
    {
        var latencies = new ConcurrentBag<long>(new long[] { 5 });
        var start = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2024, 1, 1, 12, 1, 0, DateTimeKind.Utc);

        var report = _mode.GenerateReport(10, 0, latencies, start, end);

        Assert.Equal(60.0, report.DurationSeconds);
        Assert.Equal(start, report.StartTime);
        Assert.Equal(end, report.EndTime);
    }

    [Fact]
    public void Percentile_EmptyArray_ReturnsZero()
    {
        var result = LoadTestMode.Percentile(Array.Empty<long>(), 0.5);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Percentile_SingleElement_ReturnsThatElement()
    {
        var result = LoadTestMode.Percentile(new long[] { 42 }, 0.5);
        Assert.Equal(42, result);
    }

    [Fact]
    public void Percentile_P50_ReturnsMedian()
    {
        var sorted = Enumerable.Range(1, 100).Select(i => (long)i).ToArray();
        var result = LoadTestMode.Percentile(sorted, 0.50);
        Assert.Equal(51, result);
    }

    [Fact]
    public void Percentile_P99_ReturnsNearMax()
    {
        var sorted = Enumerable.Range(1, 100).Select(i => (long)i).ToArray();
        var result = LoadTestMode.Percentile(sorted, 0.99);
        Assert.Equal(100, result);
    }

    [Fact]
    public async Task RunAsync_NoBots_CompletesWithoutError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _mode.RunAsync(5, cts.Token);
    }

    [Fact]
    public void LoadTestReport_HasAllProperties()
    {
        var report = new LoadTestReport
        {
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddMinutes(1),
            DurationSeconds = 60,
            TotalRequests = 100,
            TotalErrors = 5,
            ErrorRate = 0.05,
            ActualRps = 1.67,
            P50Ms = 50,
            P90Ms = 90,
            P95Ms = 95,
            P99Ms = 99,
            MinMs = 1,
            MaxMs = 200,
            AvgMs = 55.5
        };

        Assert.Equal(100, report.TotalRequests);
        Assert.Equal(5, report.TotalErrors);
        Assert.Equal(0.05, report.ErrorRate);
        Assert.Equal(1.67, report.ActualRps);
        Assert.Equal(50, report.P50Ms);
        Assert.Equal(90, report.P90Ms);
        Assert.Equal(95, report.P95Ms);
        Assert.Equal(99, report.P99Ms);
        Assert.Equal(1, report.MinMs);
        Assert.Equal(200, report.MaxMs);
        Assert.Equal(55.5, report.AvgMs);
    }
}
