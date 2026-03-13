using System.Collections.Concurrent;
using BotService.Configuration;
using BotService.Services.Observer;
using BotService.Services.Swarm.Modes;
using Microsoft.Extensions.Options;

namespace BotService.Services.Swarm;

/// <summary>
/// Orchestrates bot swarm operations across multiple modes.
/// Manages swarm lifecycle, mode switching, and provides unified status.
/// </summary>
public class SwarmOrchestrator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly BotObserver _observer;
    private readonly BotServiceOptions _config;
    private readonly ILogger<SwarmOrchestrator> _logger;
    
    private readonly ConcurrentDictionary<string, SwarmMode> _activeModes = new();
    private readonly ConcurrentDictionary<string, SwarmModeStatus> _modeStatuses = new();
    private SwarmStatus _swarmStatus = SwarmStatus.Idle;
    private DateTime _startedAt;

    public SwarmOrchestrator(
        IServiceProvider serviceProvider,
        BotObserver observer,
        IOptions<BotServiceOptions> config,
        ILogger<SwarmOrchestrator> logger)
    {
        _serviceProvider = serviceProvider;
        _observer = observer;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>Start the swarm with specified mode</summary>
    public async Task<SwarmStartResult> StartAsync(SwarmStartRequest request, CancellationToken ct = default)
    {
        if (_swarmStatus == SwarmStatus.Running)
            return new SwarmStartResult { Success = false, Message = "Swarm is already running" };

        _swarmStatus = SwarmStatus.Starting;
        _startedAt = DateTime.UtcNow;

        try
        {
            var mode = CreateMode(request.Mode);
            if (mode == null)
                return new SwarmStartResult { Success = false, Message = $"Unknown mode: {request.Mode}" };

            await mode.InitializeAsync(request.Parameters, ct);
            _activeModes[request.Mode] = mode;
            _modeStatuses[request.Mode] = new SwarmModeStatus
            {
                Mode = request.Mode,
                Status = "running",
                StartedAt = DateTime.UtcNow,
                BotCount = request.BotCount
            };

            _ = Task.Run(() => RunModeAsync(request.Mode, mode, request.BotCount, ct), ct);

            _swarmStatus = SwarmStatus.Running;
            _logger.LogInformation("Swarm started in {Mode} mode with {BotCount} bots",
                request.Mode, request.BotCount);

            return new SwarmStartResult
            {
                Success = true,
                Message = $"Swarm started in {request.Mode} mode with {request.BotCount} bots"
            };
        }
        catch (Exception ex)
        {
            _swarmStatus = SwarmStatus.Error;
            _logger.LogError(ex, "Failed to start swarm in {Mode} mode", request.Mode);
            return new SwarmStartResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>Stop all running swarm modes</summary>
    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping swarm ({Modes} active modes)", _activeModes.Count);
        _swarmStatus = SwarmStatus.Stopping;

        foreach (var (name, mode) in _activeModes)
        {
            try
            {
                await mode.StopAsync();
                _modeStatuses[name] = _modeStatuses[name] with { Status = "stopped" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping mode {Mode}", name);
            }
        }

        _activeModes.Clear();
        _swarmStatus = SwarmStatus.Idle;
        _logger.LogInformation("Swarm stopped");
    }

    /// <summary>Get current swarm status</summary>
    public SwarmStatusResponse GetStatus()
    {
        return new SwarmStatusResponse
        {
            Status = _swarmStatus.ToString().ToLower(),
            StartedAt = _startedAt,
            UptimeMinutes = _swarmStatus == SwarmStatus.Running
                ? (int)(DateTime.UtcNow - _startedAt).TotalMinutes
                : 0,
            ActiveModes = _modeStatuses.Values.ToList(),
            RecentFindings = _observer.GetRecentFindings(10).Count,
            TotalBots = _modeStatuses.Values.Sum(m => m.BotCount)
        };
    }

    private SwarmMode? CreateMode(string modeName)
    {
        return modeName.ToLowerInvariant() switch
        {
            "onboarding" => new OnboardingAssistMode(_serviceProvider, _observer, _logger),
            "retention" => new RetentionBoostMode(_serviceProvider, _observer, _logger),
            "loadtest" => new LoadTestMode(_serviceProvider, _observer, _logger),
            "experiment" => new ExperimentMode(_serviceProvider, _observer, _logger),
            _ => null
        };
    }

    private async Task RunModeAsync(string name, SwarmMode mode, int botCount, CancellationToken ct)
    {
        try
        {
            await mode.RunAsync(botCount, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Swarm mode {Mode} cancelled", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Swarm mode {Mode} failed", name);
            await _observer.RecordObservation(
                Models.FindingType.ServerError, Models.FindingSeverity.High,
                $"Swarm mode {name} crashed", ex.Message, "BotService", "swarm", "");
        }
        finally
        {
            if (_modeStatuses.TryGetValue(name, out var status))
                _modeStatuses[name] = status with { Status = "completed" };
        }
    }
}

// ── Models ──

public enum SwarmStatus { Idle, Starting, Running, Stopping, Error }

public class SwarmStartRequest
{
    public string Mode { get; set; } = "onboarding";
    public int BotCount { get; set; } = 5;
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public class SwarmStartResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public record SwarmModeStatus
{
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "idle";
    public DateTime StartedAt { get; set; }
    public int BotCount { get; set; }
}

public class SwarmStatusResponse
{
    public string Status { get; set; } = "idle";
    public DateTime StartedAt { get; set; }
    public int UptimeMinutes { get; set; }
    public List<SwarmModeStatus> ActiveModes { get; set; } = new();
    public int RecentFindings { get; set; }
    public int TotalBots { get; set; }
}
