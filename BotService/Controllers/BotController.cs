using BotService.Services;
using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BotService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BotController : ControllerBase
{
    private readonly BotDbContext _db;
    private readonly IOptionsMonitor<BotServiceOptions> _config;
    private readonly BotPersonaEngine _personaEngine;
    private readonly ILogger<BotController> _logger;

    public BotController(
        BotDbContext db,
        IOptionsMonitor<BotServiceOptions> config,
        BotPersonaEngine personaEngine,
        ILogger<BotController> logger)
    {
        _db = db;
        _config = config;
        _personaEngine = personaEngine;
        _logger = logger;
    }

    /// <summary>Get status overview of all bots</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var bots = await _db.BotStates.ToListAsync();
        var personas = _personaEngine.Personas;
        var config = _config.CurrentValue;

        return Ok(new
        {
            serviceEnabled = config.Enabled,
            modes = new
            {
                synthetic = new { enabled = config.Modes.Synthetic.Enabled, cycleIntervalSec = config.Modes.Synthetic.CycleIntervalSec },
                warmup = new { enabled = config.Modes.Warmup.Enabled, checkIntervalSec = config.Modes.Warmup.CheckIntervalSec },
                load = new { enabled = config.Modes.Load.Enabled, maxConcurrent = config.Modes.Load.MaxConcurrentBots },
                chaos = new { enabled = config.Modes.Chaos.Enabled, scenarios = config.Modes.Chaos.EnabledScenarios }
            },
            totalPersonas = personas.Count,
            bots = bots.Select(b => new
            {
                b.PersonaId,
                status = b.Status.ToString(),
                b.ProfileId,
                b.SwipesToday,
                b.MessagesSentToday,
                b.MatchCount,
                b.ConversationCount,
                b.LastAction,
                hasToken = b.AccessToken != null,
                tokenExpiresAt = b.TokenExpiresAt
            })
        });
    }

    /// <summary>Get detailed info for a specific bot</summary>
    [HttpGet("status/{personaId}")]
    public async Task<IActionResult> GetBotStatus(string personaId)
    {
        var bot = await _db.BotStates.FirstOrDefaultAsync(b => b.PersonaId == personaId);
        if (bot == null) return NotFound(new { error = $"Bot '{personaId}' not found" });

        var persona = _personaEngine.GetPersonaById(personaId);

        return Ok(new
        {
            state = new
            {
                bot.PersonaId,
                status = bot.Status.ToString(),
                bot.KeycloakUserId,
                bot.ProfileId,
                bot.SwipesToday,
                bot.MessagesSentToday,
                bot.MatchCount,
                bot.ConversationCount,
                bot.LastAction,
                hasToken = bot.AccessToken != null,
                tokenExpiresAt = bot.TokenExpiresAt
            },
            persona = persona != null ? new
            {
                persona.FirstName,
                persona.LastName,
                persona.Age,
                persona.Gender,
                persona.City,
                persona.Modes,
                persona.Behavior
            } : null
        });
    }

    /// <summary>List all loaded personas</summary>
    [HttpGet("personas")]
    public IActionResult GetPersonas()
    {
        var personas = _personaEngine.Personas;
        return Ok(personas.Select(p => new
        {
            p.Id,
            p.FirstName,
            p.LastName,
            p.Age,
            p.Gender,
            p.City,
            p.Modes,
            behaviorSummary = p.Behavior != null ? new
            {
                p.Behavior.SwipeRightProbability,
                p.Behavior.Chattiness,
                p.Behavior.MaxDailySwipes,
                p.Behavior.MaxDailyMessages
            } : null
        }));
    }

    /// <summary>Pause a specific bot</summary>
    [HttpPost("pause/{personaId}")]
    public async Task<IActionResult> PauseBot(string personaId)
    {
        var bot = await _db.BotStates.FirstOrDefaultAsync(b => b.PersonaId == personaId);
        if (bot == null) return NotFound(new { error = $"Bot '{personaId}' not found" });

        bot.Status = BotStatus.Paused;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Bot {PersonaId} paused via API", personaId);
        return Ok(new { message = $"Bot '{personaId}' paused", status = bot.Status.ToString() });
    }

    /// <summary>Resume a paused bot</summary>
    [HttpPost("resume/{personaId}")]
    public async Task<IActionResult> ResumeBot(string personaId)
    {
        var bot = await _db.BotStates.FirstOrDefaultAsync(b => b.PersonaId == personaId);
        if (bot == null) return NotFound(new { error = $"Bot '{personaId}' not found" });

        bot.Status = BotStatus.Active;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Bot {PersonaId} resumed via API", personaId);
        return Ok(new { message = $"Bot '{personaId}' resumed", status = bot.Status.ToString() });
    }

    /// <summary>Pause all bots</summary>
    [HttpPost("pause-all")]
    public async Task<IActionResult> PauseAll()
    {
        var activeBots = await _db.BotStates
            .Where(b => b.Status == BotStatus.Active)
            .ToListAsync();

        foreach (var bot in activeBots)
            bot.Status = BotStatus.Paused;

        await _db.SaveChangesAsync();

        _logger.LogInformation("All {Count} bots paused via API", activeBots.Count);
        return Ok(new { message = $"{activeBots.Count} bots paused" });
    }

    /// <summary>Resume all paused bots</summary>
    [HttpPost("resume-all")]
    public async Task<IActionResult> ResumeAll()
    {
        var pausedBots = await _db.BotStates
            .Where(b => b.Status == BotStatus.Paused)
            .ToListAsync();

        foreach (var bot in pausedBots)
            bot.Status = BotStatus.Active;

        await _db.SaveChangesAsync();

        _logger.LogInformation("All {Count} bots resumed via API", pausedBots.Count);
        return Ok(new { message = $"{pausedBots.Count} bots resumed" });
    }

    /// <summary>Reset daily counters for all bots</summary>
    [HttpPost("reset-counters")]
    public async Task<IActionResult> ResetCounters()
    {
        var bots = await _db.BotStates.ToListAsync();
        foreach (var bot in bots)
        {
            bot.SwipesToday = 0;
            bot.MessagesSentToday = 0;
            bot.CounterResetDate = DateTime.UtcNow.Date;
        }
        await _db.SaveChangesAsync();

        return Ok(new { message = $"Counters reset for {bots.Count} bots" });
    }

    /// <summary>Delete a bot and its state</summary>
    [HttpDelete("{personaId}")]
    public async Task<IActionResult> DeleteBot(string personaId)
    {
        var bot = await _db.BotStates.FirstOrDefaultAsync(b => b.PersonaId == personaId);
        if (bot == null) return NotFound(new { error = $"Bot '{personaId}' not found" });

        _db.BotStates.Remove(bot);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Bot {PersonaId} deleted via API", personaId);
        return Ok(new { message = $"Bot '{personaId}' deleted" });
    }
}
