using BotService.Services.Swarm;
using Microsoft.AspNetCore.Mvc;

namespace BotService.Controllers;

/// <summary>
/// REST API for controlling the bot swarm orchestrator.
/// Start/stop swarm modes, get status, manage experiments.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SwarmController : ControllerBase
{
    private readonly SwarmOrchestrator _orchestrator;
    private readonly ILogger<SwarmController> _logger;

    public SwarmController(SwarmOrchestrator orchestrator, ILogger<SwarmController> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>Get current swarm status</summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(_orchestrator.GetStatus());
    }

    /// <summary>Start the swarm with a specific mode</summary>
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] SwarmStartRequest request)
    {
        _logger.LogInformation("Swarm start requested: mode={Mode}, bots={Bots}",
            request.Mode, request.BotCount);
        
        var result = await _orchestrator.StartAsync(request);
        
        if (!result.Success)
            return BadRequest(result);
        
        return Ok(result);
    }

    /// <summary>Stop all running swarm modes</summary>
    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        _logger.LogInformation("Swarm stop requested");
        await _orchestrator.StopAsync();
        return Ok(new { message = "Swarm stopped" });
    }

    /// <summary>List available swarm modes</summary>
    [HttpGet("modes")]
    public IActionResult GetModes()
    {
        return Ok(new[]
        {
            new { name = "onboarding", description = "Simulate new user onboarding flow" },
            new { name = "retention", description = "Engage inactive users to boost retention" },
            new { name = "loadtest", description = "High-volume API stress testing" },
            new { name = "experiment", description = "A/B testing framework for bot behaviors" }
        });
    }
}
