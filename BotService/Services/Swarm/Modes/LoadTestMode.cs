using System.Diagnostics;
using BotService.Data;
using BotService.Models;
using BotService.Services.Content;
using BotService.Services.Observer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BotService.Services.Swarm.Modes;

/// <summary>
/// Load test mode: drives real API traffic at configurable concurrency.
/// Uses canned engine (no LLM cost) and measures latency percentiles,
/// error rates, and throughput across all service endpoints.
/// Generates a structured load test report at completion.
/// </summary>
public class LoadTestMode : SwarmMode
{
    public override string Name => "loadtest";
    public override string Description => "Real API load test with latency tracking and report generation";

    private int _targetRps = 5;
    private int _durationSeconds = 60;

    public LoadTestMode(IServiceProvider sp, BotObserver observer, ILogger logger)
        : base(sp, observer, logger) { }

    public override Task InitializeAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        if (parameters.TryGetValue("targetRps", out var rps) && int.TryParse(rps, out var r))
            _targetRps = r;
        if (parameters.TryGetValue("durationSeconds", out var dur) && int.TryParse(dur, out var d))
            _durationSeconds = d;

        return base.InitializeAsync(parameters, ct);
    }

    protected override async Task ExecuteAsync(int botCount, CancellationToken ct)
    {
        Logger.LogInformation("[LoadTest] Starting: {Bots} bots, target {Rps} RPS, {Duration}s duration",
            botCount, _targetRps, _durationSeconds);

        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();

        var activeBots = await db.BotStates
            .Where(b => b.Status == BotStatus.Active && b.AccessToken != null && b.ProfileId != null)
            .Take(botCount)
            .ToListAsync(ct);

        if (activeBots.Count == 0)
        {
            Logger.LogWarning("[LoadTest] No active bots available for load test");
            return;
        }

        var latencies = new System.Collections.Concurrent.ConcurrentBag<long>();
        var errors = 0;
        var requests = 0;
        var startTime = DateTime.UtcNow;
        var endTime = startTime.AddSeconds(_durationSeconds);
        var delayPerRequest = botCount > 0 ? 1000 / Math.Max(_targetRps / botCount, 1) : 200;

        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timerCts.CancelAfter(TimeSpan.FromSeconds(_durationSeconds + 5));

        var tasks = activeBots.Select(async bot =>
        {
            apiClient.SetBotContext(bot.PersonaId, bot.KeycloakUserId ?? "");

            while (DateTime.UtcNow < endTime && !timerCts.Token.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    // Cycle through real API operations
                    var op = Interlocked.Increment(ref requests) % 4;
                    switch (op)
                    {
                        case 0: // Discover candidates
                            await apiClient.GetCandidatesAsync(bot.ProfileId!.Value, bot.AccessToken!, timerCts.Token);
                            break;
                        case 1: // Get matches
                            await apiClient.GetMatchesAsync(bot.ProfileId!.Value, bot.AccessToken!, timerCts.Token);
                            break;
                        case 2: // Swipe (right on random candidate)
                            var candidates = await apiClient.GetCandidatesAsync(
                                bot.ProfileId!.Value, bot.AccessToken!, timerCts.Token);
                            if (candidates.Length > 0)
                            {
                                var c = candidates[Random.Shared.Next(candidates.Length)];
                                var tid = c.TryGetProperty("id", out var idp) ? idp.GetInt32() : 0;
                                if (tid > 0)
                                    await apiClient.SwipeAsync(bot.ProfileId.Value, tid, true, bot.AccessToken!, timerCts.Token);
                            }
                            break;
                        case 3: // Get conversation messages (if matched)
                            var matches = await apiClient.GetMatchesAsync(bot.ProfileId!.Value, bot.AccessToken!, timerCts.Token);
                            if (matches.Length > 0)
                            {
                                var m = matches[0];
                                if (m.TryGetProperty("keycloakUserId", out var kcProp))
                                {
                                    var uid = kcProp.GetString();
                                    if (!string.IsNullOrEmpty(uid))
                                        await apiClient.GetConversationMessagesAsync(uid, bot.AccessToken!, timerCts.Token);
                                }
                            }
                            break;
                    }

                    sw.Stop();
                    latencies.Add(sw.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception)
                {
                    sw.Stop();
                    latencies.Add(sw.ElapsedMilliseconds);
                    Interlocked.Increment(ref errors);
                }

                await Task.Delay(delayPerRequest, timerCts.Token).ConfigureAwait(false);
            }
        });

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }

        // Generate report
        var report = GenerateReport(requests, errors, latencies, startTime, DateTime.UtcNow);
        LogReport(report);

        await Observer.RecordObservation(
            FindingType.PerformanceTrend, FindingSeverity.Info,
            $"Load test completed: {requests} requests, {errors} errors, p50={report.P50Ms}ms, p99={report.P99Ms}ms",
            $"Duration: {report.DurationSeconds:F1}s, RPS: {report.ActualRps:F1}, Error rate: {report.ErrorRate:P1}",
            "bot-service", "loadtest-mode", "");
    }

    internal LoadTestReport GenerateReport(
        int totalRequests, int totalErrors,
        System.Collections.Concurrent.ConcurrentBag<long> latencies,
        DateTime start, DateTime end)
    {
        var sorted = latencies.OrderBy(l => l).ToArray();
        return new LoadTestReport
        {
            StartTime = start,
            EndTime = end,
            DurationSeconds = (end - start).TotalSeconds,
            TotalRequests = totalRequests,
            TotalErrors = totalErrors,
            ErrorRate = totalRequests > 0 ? (double)totalErrors / totalRequests : 0,
            ActualRps = (end - start).TotalSeconds > 0 ? totalRequests / (end - start).TotalSeconds : 0,
            P50Ms = Percentile(sorted, 0.50),
            P90Ms = Percentile(sorted, 0.90),
            P95Ms = Percentile(sorted, 0.95),
            P99Ms = Percentile(sorted, 0.99),
            MinMs = sorted.Length > 0 ? sorted[0] : 0,
            MaxMs = sorted.Length > 0 ? sorted[^1] : 0,
            AvgMs = sorted.Length > 0 ? sorted.Average() : 0
        };
    }

    internal static long Percentile(long[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[(int)(sorted.Length * p)];

    private void LogReport(LoadTestReport r)
    {
        Logger.LogWarning(
            "[LoadTest] REPORT: {Requests} requests in {Duration:F1}s ({Rps:F1} RPS). " +
            "Errors: {Errors} ({ErrorRate:P1}). " +
            "Latency: p50={P50}ms p90={P90}ms p95={P95}ms p99={P99}ms min={Min}ms max={Max}ms avg={Avg:F0}ms",
            r.TotalRequests, r.DurationSeconds, r.ActualRps,
            r.TotalErrors, r.ErrorRate,
            r.P50Ms, r.P90Ms, r.P95Ms, r.P99Ms, r.MinMs, r.MaxMs, r.AvgMs);
    }
}

public class LoadTestReport
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double DurationSeconds { get; set; }
    public int TotalRequests { get; set; }
    public int TotalErrors { get; set; }
    public double ErrorRate { get; set; }
    public double ActualRps { get; set; }
    public long P50Ms { get; set; }
    public long P90Ms { get; set; }
    public long P95Ms { get; set; }
    public long P99Ms { get; set; }
    public long MinMs { get; set; }
    public long MaxMs { get; set; }
    public double AvgMs { get; set; }
}
