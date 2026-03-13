using BotService.Services.Observer;

namespace BotService.Services.Swarm.Modes;

/// <summary>
/// Base class for all swarm operation modes.
/// Each mode defines a specific bot behavior pattern.
/// </summary>
public abstract class SwarmMode
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly BotObserver Observer;
    protected readonly ILogger Logger;
    protected bool IsRunning;
    protected CancellationTokenSource? Cts;

    protected SwarmMode(IServiceProvider serviceProvider, BotObserver observer, ILogger logger)
    {
        ServiceProvider = serviceProvider;
        Observer = observer;
        Logger = logger;
    }

    /// <summary>Mode identifier</summary>
    public abstract string Name { get; }
    
    /// <summary>Human-readable description</summary>
    public abstract string Description { get; }

    /// <summary>Initialize mode with optional parameters</summary>
    public virtual Task InitializeAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return Task.CompletedTask;
    }

    /// <summary>Run the mode with the specified number of bots</summary>
    public async Task RunAsync(int botCount, CancellationToken ct)
    {
        IsRunning = true;
        Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        Logger.LogInformation("Starting swarm mode {Mode} with {BotCount} bots", Name, botCount);
        
        try
        {
            await ExecuteAsync(botCount, Cts.Token);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Stop the mode gracefully</summary>
    public Task StopAsync()
    {
        Logger.LogInformation("Stopping swarm mode {Mode}", Name);
        Cts?.Cancel();
        IsRunning = false;
        return Task.CompletedTask;
    }

    /// <summary>Implement mode-specific logic here</summary>
    protected abstract Task ExecuteAsync(int botCount, CancellationToken ct);
}
