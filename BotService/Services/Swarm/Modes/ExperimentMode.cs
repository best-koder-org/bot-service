using System.Text.Json;
using BotService.Data;
using BotService.Models;
using BotService.Services.Observer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BotService.Services.Swarm.Modes;

/// <summary>
/// Experiment mode: A/B testing framework for bot behaviors.
/// Splits bots into groups A/B based on an Experiment entity, runs each
/// group with its configured strategy, and records per-group metrics.
/// Supports real DatingApp API interactions per group config.
/// </summary>
public class ExperimentMode : SwarmMode
{
    public override string Name => "experiment";
    public override string Description => "A/B testing framework for bot behavior experiments";

    private int _experimentId;

    public ExperimentMode(IServiceProvider sp, BotObserver observer, ILogger logger)
        : base(sp, observer, logger) { }

    public override Task InitializeAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        if (parameters.TryGetValue("experimentId", out var idStr) && int.TryParse(idStr, out var id))
            _experimentId = id;

        return base.InitializeAsync(parameters, ct);
    }

    protected override async Task ExecuteAsync(int botCount, CancellationToken ct)
    {
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var apiClient = scope.ServiceProvider.GetRequiredService<DatingAppApiClient>();

        var experiment = await db.Experiments.FindAsync(new object[] { _experimentId }, ct);
        if (experiment == null)
        {
            Logger.LogWarning("[Experiment] Experiment {Id} not found", _experimentId);
            return;
        }

        if (experiment.Status != ExperimentStatus.Running)
        {
            Logger.LogWarning("[Experiment] Experiment {Id} is {Status}, not Running", _experimentId, experiment.Status);
            return;
        }

        var botsPerGroup = experiment.BotsPerGroup;
        var activeBots = await db.BotStates
            .Where(b => b.Status == BotStatus.Active && b.AccessToken != null && b.ProfileId != null)
            .Take(botsPerGroup * 2)
            .ToListAsync(ct);

        if (activeBots.Count < 2)
        {
            Logger.LogWarning("[Experiment] Need at least 2 bots, have {Count}", activeBots.Count);
            return;
        }

        var groupABots = activeBots.Take(Math.Min(botsPerGroup, activeBots.Count / 2)).ToList();
        var groupBBots = activeBots.Skip(groupABots.Count).Take(botsPerGroup).ToList();

        var configA = ParseConfig(experiment.GroupAConfig);
        var configB = ParseConfig(experiment.GroupBConfig);

        Logger.LogInformation(
            "[Experiment] Starting '{Name}': Group A ({ACount} bots), Group B ({BCount} bots)",
            experiment.Name, groupABots.Count, groupBBots.Count);

        var metricsA = new ExperimentMetrics { Group = "A" };
        var metricsB = new ExperimentMetrics { Group = "B" };

        var taskA = RunGroup(groupABots, configA, metricsA, apiClient, ct);
        var taskB = RunGroup(groupBBots, configB, metricsB, apiClient, ct);

        await Task.WhenAll(taskA, taskB);

        // Store metrics on the experiment
        var results = new
        {
            groupA = new { metricsA.Interactions, metricsA.Successes, metricsA.SuccessRate, metricsA.TotalLatencyMs },
            groupB = new { metricsB.Interactions, metricsB.Successes, metricsB.SuccessRate, metricsB.TotalLatencyMs }
        };
        experiment.MetricsJson = JsonSerializer.Serialize(results);

        if (metricsA.Interactions > 0 && metricsB.Interactions > 0)
            experiment.Winner = metricsA.SuccessRate > metricsB.SuccessRate ? "A" : "B";

        await db.SaveChangesAsync(ct);

        Logger.LogInformation(
            "[Experiment] '{Name}' complete. A: {AI}/{AT} ({AR:P1}), B: {BI}/{BT} ({BR:P1}). Winner: {W}",
            experiment.Name,
            metricsA.Successes, metricsA.Interactions, metricsA.SuccessRate,
            metricsB.Successes, metricsB.Interactions, metricsB.SuccessRate,
            experiment.Winner ?? "none");

        await Observer.RecordObservation(
            FindingType.UxObservation, FindingSeverity.Info,
            $"Experiment '{experiment.Name}' completed — winner: {experiment.Winner ?? "none"}",
            $"A: {metricsA.SuccessRate:P1} ({metricsA.Interactions} interactions), B: {metricsB.SuccessRate:P1} ({metricsB.Interactions} interactions)",
            "BotService", "experiment-mode", "",
            results);
    }

    private async Task RunGroup(
        List<BotState> bots, ExperimentConfig config,
        ExperimentMetrics metrics, DatingAppApiClient apiClient, CancellationToken ct)
    {
        var tasks = bots.Select(bot => RunBotWithConfig(bot, config, metrics, apiClient, ct));
        await Task.WhenAll(tasks);
    }

    private async Task RunBotWithConfig(
        BotState bot, ExperimentConfig config,
        ExperimentMetrics metrics, DatingAppApiClient apiClient, CancellationToken ct)
    {
        apiClient.SetBotContext(bot.PersonaId, bot.KeycloakUserId ?? "");
        var rounds = config.Rounds;

        for (var round = 0; round < rounds && !ct.IsCancellationRequested; round++)
        {
            try
            {
                // Apply configured delay between rounds
                await Task.Delay(config.DelayMs, ct);

                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Try to discover and swipe
                var candidates = await apiClient.GetCandidatesAsync(bot.ProfileId!.Value, bot.AccessToken!, ct);
                Interlocked.Increment(ref metrics.Interactions);

                if (candidates.Length > 0)
                {
                    var target = candidates[Random.Shared.Next(candidates.Length)];
                    var tid = target.TryGetProperty("id", out var idp) ? idp.GetInt32() : 0;
                    if (tid > 0)
                    {
                        // Apply swipe strategy: swipe right based on configured probability
                        var swipeRight = Random.Shared.NextDouble() < config.SwipeRightProbability;
                        await apiClient.SwipeAsync(bot.ProfileId.Value, tid, swipeRight, bot.AccessToken!, ct);
                        Interlocked.Increment(ref metrics.Interactions);

                        if (swipeRight)
                        {
                            // Check if we got a match (success = got a match from swiping)
                            var matches = await apiClient.GetMatchesAsync(bot.ProfileId.Value, bot.AccessToken!, ct);
                            if (matches.Length > 0)
                                Interlocked.Increment(ref metrics.Successes);
                        }
                    }
                }

                sw.Stop();
                Interlocked.Add(ref metrics.TotalLatencyMs, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "[Experiment] Bot {Persona} round {Round} error", bot.PersonaId, round);
            }
        }
    }

    internal static ExperimentConfig ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ExperimentConfig();
        try
        {
            return JsonSerializer.Deserialize<ExperimentConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ExperimentConfig();
        }
        catch { return new ExperimentConfig(); }
    }
}

public class ExperimentConfig
{
    public int Rounds { get; set; } = 5;
    public int DelayMs { get; set; } = 2000;
    public double SwipeRightProbability { get; set; } = 0.7;
}

public class ExperimentMetrics
{
    public string Group { get; set; } = "";
    public int Interactions;
    public int Successes;
    public long TotalLatencyMs;
    public double SuccessRate => Interactions > 0 ? (double)Successes / Interactions : 0;
}
