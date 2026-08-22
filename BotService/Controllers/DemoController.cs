using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using BotService.Services.Onboarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BotService.Controllers;

/// <summary>
/// \"Tester Demo Mode\" control surface: enable/disable reactive fake-user mode,
/// inspect bot activity, and trigger the targeted bot-data purge.
/// </summary>
[ApiController]
[Route("api/demo")]
public class DemoController : ControllerBase
{
    private readonly DemoRuntimeState _demoState;
    private readonly DemoDataPurgeService _purgeService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DemoController> _logger;

    public DemoController(
        DemoRuntimeState demoState,
        DemoDataPurgeService purgeService,
        IServiceProvider serviceProvider,
        ILogger<DemoController> logger)
    {
        _demoState = demoState;
        _purgeService = purgeService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>Current demo-mode status + bot activity summary.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var activeBots = await db.BotStates.CountAsync(b => b.Status == BotStatus.Active, ct);
        var totalMatches = await db.BotStates.SumAsync(b => (int?)b.MatchCount ?? 0, ct);
        var totalMessages = await db.BotStates.SumAsync(b => (int?)b.ConversationCount ?? 0, ct);
        var onboardedTesters = await db.OnboardingTargets.CountAsync(ct);

        return Ok(new
        {
            demoEnabled = _demoState.Enabled,
            reactiveOnly = _demoState.ReactiveOnly,
            activeBots,
            totalBotMatches = totalMatches,
            totalBotMessages = totalMessages,
            onboardedTesters
        });
    }

    /// <summary>Enable demo mode (bots become reactive fake users).</summary>
    [HttpPost("enable")]
    public IActionResult Enable([FromBody] DemoEnableRequest? request)
    {
        _demoState.Enabled = request?.Enabled ?? true;
        _demoState.ReactiveOnly = request?.ReactiveOnly ?? true;
        _logger.LogInformation("Demo mode {State} (reactiveOnly={ReactiveOnly})",
            _demoState.Enabled ? "enabled" : "disabled", _demoState.ReactiveOnly);
        return Ok(new { demoEnabled = _demoState.Enabled, reactiveOnly = _demoState.ReactiveOnly });
    }

    /// <summary>Disable demo mode; optionally purge all bot interactions first (PurgeOnStop).</summary>
    [HttpPost("disable")]
    public async Task<IActionResult> Disable(CancellationToken ct)
    {
        _demoState.Enabled = false;

        var demo = _serviceProvider
            .GetRequiredService<IOptionsMonitor<BotServiceOptions>>().CurrentValue.Demo;
        if (demo.PurgeOnStop)
        {
            var ok = await _purgeService.PurgeAllAsync(ct);
            _logger.LogInformation("Purge on demo stop: {Result}", ok ? "ok" : "failed");
        }

        return Ok(new { demoEnabled = false });
    }

    /// <summary>Trigger the targeted bot-data purge (optional TTL filter).</summary>
    [HttpPost("purge")]
    public async Task<IActionResult> Purge([FromQuery] int olderThanHours = 0, CancellationToken ct = default)
    {
        var ok = olderThanHours > 0
            ? await _purgeService.PurgeAsync(ct, olderThanHours)
            : await _purgeService.PurgeAllAsync(ct);
        return Ok(new { purged = ok });
    }
}

public class DemoEnableRequest
{
    public bool? Enabled { get; set; }
    public bool? ReactiveOnly { get; set; }
}
