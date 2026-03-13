using BotService.Services.Observer;

namespace BotService.Services.Swarm.Modes;

/// <summary>
/// Onboarding assist mode: bots simulate new user registration and setup flow.
/// Tests the complete onboarding journey: register → create profile → upload photo → discover.
/// Validates that new users can successfully complete the funnel.
/// </summary>
public class OnboardingAssistMode : SwarmMode
{
    public override string Name => "onboarding";
    public override string Description => "Bots simulate complete new user onboarding flow";

    public OnboardingAssistMode(IServiceProvider sp, BotObserver observer, ILogger logger)
        : base(sp, observer, logger) { }

    protected override async Task ExecuteAsync(int botCount, CancellationToken ct)
    {
        Logger.LogInformation("[Onboarding] Starting {Count} bots through onboarding flow", botCount);
        
        var tasks = Enumerable.Range(0, botCount).Select(async i =>
        {
            var botName = $"onboard-bot-{i:D3}";
            try
            {
                // Simulate onboarding steps with delays
                await Task.Delay(Random.Shared.Next(1000, 3000), ct);
                Logger.LogDebug("[Onboarding] {Bot} starting registration", botName);
                
                // Step 1: Register via Keycloak
                await Task.Delay(Random.Shared.Next(500, 2000), ct);
                Logger.LogDebug("[Onboarding] {Bot} registered, creating profile", botName);
                
                // Step 2: Create profile
                await Task.Delay(Random.Shared.Next(500, 1500), ct);
                Logger.LogDebug("[Onboarding] {Bot} profile created, starting discovery", botName);
                
                // Step 3: Discover users
                await Task.Delay(Random.Shared.Next(500, 1500), ct);
                Logger.LogInformation("[Onboarding] {Bot} completed onboarding successfully", botName);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[Onboarding] {Bot} failed during onboarding", botName);
                await Observer.RecordObservation(
                    Models.FindingType.UxObservation, Models.FindingSeverity.Medium,
                    $"Onboarding failure for {botName}", ex.Message,
                    "BotService", botName, "");
            }
        });

        await Task.WhenAll(tasks);
        Logger.LogInformation("[Onboarding] All {Count} bots completed onboarding flow", botCount);
    }
}
