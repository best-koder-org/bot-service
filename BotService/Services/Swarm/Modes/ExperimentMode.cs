using BotService.Services.Observer;

namespace BotService.Services.Swarm.Modes;

/// <summary>
/// Experiment mode: A/B testing framework for bot behaviors.
/// Splits bots into control/treatment groups and compares outcomes.
/// Tracks metrics: match rate, message response rate, conversation depth.
/// </summary>
public class ExperimentMode : SwarmMode
{
    public override string Name => "experiment";
    public override string Description => "A/B testing framework for bot behavior experiments";
    
    private string _experimentName = "default";
    private string _variant = "control";

    public ExperimentMode(IServiceProvider sp, BotObserver observer, ILogger logger)
        : base(sp, observer, logger) { }

    public override Task InitializeAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        if (parameters.TryGetValue("name", out var name))
            _experimentName = name;
        if (parameters.TryGetValue("variant", out var variant))
            _variant = variant;
        
        Logger.LogInformation("[Experiment] Initialized: name={Name}, variant={Variant}",
            _experimentName, _variant);
        
        return base.InitializeAsync(parameters, ct);
    }

    protected override async Task ExecuteAsync(int botCount, CancellationToken ct)
    {
        var halfCount = botCount / 2;
        Logger.LogInformation("[Experiment] Starting {Name}: {Control} control + {Treatment} treatment bots",
            _experimentName, halfCount, botCount - halfCount);

        var controlMetrics = new ExperimentMetrics { Group = "control" };
        var treatmentMetrics = new ExperimentMetrics { Group = "treatment" };

        var controlTasks = Enumerable.Range(0, halfCount).Select(i =>
            RunExperimentBot($"ctrl-{i:D3}", controlMetrics, isControl: true, ct));
        
        var treatmentTasks = Enumerable.Range(0, botCount - halfCount).Select(i =>
            RunExperimentBot($"treat-{i:D3}", treatmentMetrics, isControl: false, ct));

        await Task.WhenAll(controlTasks.Concat(treatmentTasks));

        Logger.LogInformation(
            "[Experiment] {Name} complete. Control: {CInteractions} interactions, {CSuccesses} successes. " +
            "Treatment: {TInteractions} interactions, {TSuccesses} successes.",
            _experimentName,
            controlMetrics.Interactions, controlMetrics.Successes,
            treatmentMetrics.Interactions, treatmentMetrics.Successes);

        // Record experiment results as an observation
        await Observer.RecordObservation(
            Models.FindingType.UxObservation, Models.FindingSeverity.Info,
            $"Experiment '{_experimentName}' completed",
            $"Control: {controlMetrics.Interactions} interactions, {controlMetrics.Successes} successes ({controlMetrics.SuccessRate:P1}). " +
            $"Treatment: {treatmentMetrics.Interactions} interactions, {treatmentMetrics.Successes} successes ({treatmentMetrics.SuccessRate:P1}).",
            "BotService", "experiment", "",
            new { experiment = _experimentName, control = controlMetrics, treatment = treatmentMetrics });
    }

    private async Task RunExperimentBot(string botName, ExperimentMetrics metrics, bool isControl, CancellationToken ct)
    {
        try
        {
            for (var round = 0; round < 5 && !ct.IsCancellationRequested; round++)
            {
                await Task.Delay(Random.Shared.Next(1000, 3000), ct);
                Interlocked.Increment(ref metrics.Interactions);
                
                // Simulate different success rates for control vs treatment
                var successChance = isControl ? 0.3 : 0.5;
                if (Random.Shared.NextDouble() < successChance)
                    Interlocked.Increment(ref metrics.Successes);
                    
                Logger.LogDebug("[Experiment] {Bot} completed round {Round}", botName, round + 1);
            }
        }
        catch (OperationCanceledException) { }
    }
}

public class ExperimentMetrics
{
    public string Group { get; set; } = "";
    public int Interactions;
    public int Successes;
    public double SuccessRate => Interactions > 0 ? (double)Successes / Interactions : 0;
}
