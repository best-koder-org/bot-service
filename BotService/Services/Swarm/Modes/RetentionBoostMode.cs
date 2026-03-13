using BotService.Services.Observer;

namespace BotService.Services.Swarm.Modes;

/// <summary>
/// Retention boost mode: bots engage with real users who haven't been active recently.
/// Sends matches and messages to draw users back into the app.
/// Focuses on user re-engagement patterns.
/// </summary>
public class RetentionBoostMode : SwarmMode
{
    public override string Name => "retention";
    public override string Description => "Bots engage inactive users to boost retention";

    public RetentionBoostMode(IServiceProvider sp, BotObserver observer, ILogger logger)
        : base(sp, observer, logger) { }

    protected override async Task ExecuteAsync(int botCount, CancellationToken ct)
    {
        Logger.LogInformation("[Retention] Starting retention boost with {Count} bots", botCount);
        
        var cycleCount = 0;
        while (!ct.IsCancellationRequested)
        {
            cycleCount++;
            Logger.LogInformation("[Retention] Cycle {Cycle}: {Count} bots engaging users", cycleCount, botCount);
            
            // Each cycle: bots swipe on users, send messages to matches
            var tasks = Enumerable.Range(0, botCount).Select(async i =>
            {
                try
                {
                    // Simulate discovering and engaging a user
                    await Task.Delay(Random.Shared.Next(2000, 5000), ct);
                    Logger.LogDebug("[Retention] Bot-{Id} engaged a user in cycle {Cycle}", i, cycleCount);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[Retention] Bot-{Id} failed in cycle {Cycle}", i, cycleCount);
                }
            });

            await Task.WhenAll(tasks);
            
            // Wait between cycles
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
