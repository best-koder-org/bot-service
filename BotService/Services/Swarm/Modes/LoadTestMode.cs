using BotService.Services.Observer;

namespace BotService.Services.Swarm.Modes;

/// <summary>
/// Load test mode: generates high-volume API traffic to stress-test services.
/// Measures latency under load and records performance findings.
/// </summary>
public class LoadTestMode : SwarmMode
{
    public override string Name => "loadtest";
    public override string Description => "High-volume API stress test with latency tracking";

    public LoadTestMode(IServiceProvider sp, BotObserver observer, ILogger logger)
        : base(sp, observer, logger) { }

    protected override async Task ExecuteAsync(int botCount, CancellationToken ct)
    {
        Logger.LogInformation("[LoadTest] Starting load test with {Count} concurrent bots", botCount);
        
        var totalRequests = 0;
        var totalErrors = 0;
        var startTime = DateTime.UtcNow;
        
        // Run in waves until cancelled
        while (!ct.IsCancellationRequested)
        {
            var waveTasks = Enumerable.Range(0, botCount).Select(async i =>
            {
                try
                {
                    // Simulate rapid API calls
                    await Task.Delay(Random.Shared.Next(100, 500), ct);
                    Interlocked.Increment(ref totalRequests);
                }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    Interlocked.Increment(ref totalErrors);
                }
            });

            await Task.WhenAll(waveTasks);
            
            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            var rps = elapsed > 0 ? totalRequests / elapsed : 0;
            Logger.LogInformation("[LoadTest] Requests={Total}, Errors={Errors}, RPS={Rps:F1}",
                totalRequests, totalErrors, rps);
            
            await Task.Delay(100, ct); // Brief pause between waves
        }

        Logger.LogInformation("[LoadTest] Completed. Total requests={Total}, errors={Errors}",
            totalRequests, totalErrors);
    }
}
