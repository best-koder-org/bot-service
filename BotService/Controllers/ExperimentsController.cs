using BotService.Data;
using BotService.Models;
using Microsoft.AspNetCore.Mvc;
using BotService.Services.Swarm;
using Microsoft.EntityFrameworkCore;

namespace BotService.Controllers;

/// <summary>
/// REST API for managing A/B test experiments.
/// Create experiments, list them, and compare group results.
/// </summary>
[ApiController]
[Route("api/bot/[controller]")]
public class ExperimentsController : ControllerBase
{
    private readonly BotDbContext _db;
    private readonly ILogger<ExperimentsController> _logger;

    public ExperimentsController(BotDbContext db, ILogger<ExperimentsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Create a new experiment</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateExperimentRequest request)
    {
        var experiment = new Experiment
        {
            Name = request.Name,
            Description = request.Description,
            GroupAConfig = request.GroupAConfig ?? "{}",
            GroupBConfig = request.GroupBConfig ?? "{}",
            BotsPerGroup = request.BotsPerGroup > 0 ? request.BotsPerGroup : 5,
            Status = ExperimentStatus.Draft,
            CreatedAt = DateTime.UtcNow
        };

        _db.Experiments.Add(experiment);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Experiment {Id} created: {Name}", experiment.Id, experiment.Name);

        return CreatedAtAction(nameof(Get), new { id = experiment.Id }, experiment);
    }

    /// <summary>List all experiments</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] ExperimentStatus? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = _db.Experiments.AsQueryable();
        if (status.HasValue)
            query = query.Where(e => e.Status == status.Value);

        var total = await query.CountAsync();
        var experiments = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { total, page, pageSize, experiments });
    }

    /// <summary>Get experiment by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var experiment = await _db.Experiments.FindAsync(id);
        if (experiment == null) return NotFound();
        return Ok(experiment);
    }

    /// <summary>Start an experiment</summary>
    [HttpPost("{id:int}/start")]
    public async Task<IActionResult> Start(int id)
    {
        var experiment = await _db.Experiments.FindAsync(id);
        if (experiment == null) return NotFound();
        if (experiment.Status != ExperimentStatus.Draft)
            return BadRequest(new { error = $"Cannot start experiment in {experiment.Status} status" });

        experiment.Status = ExperimentStatus.Running;
        experiment.StartedAt = DateTime.UtcNow;
        experiment.EndsAt = DateTime.UtcNow.AddDays(7); // Default 7d duration
        await _db.SaveChangesAsync();

        _logger.LogInformation("Experiment {Id} started", id);
        return Ok(experiment);
    }

    /// <summary>Complete an experiment</summary>
    [HttpPost("{id:int}/complete")]
    public async Task<IActionResult> Complete(int id)
    {
        var experiment = await _db.Experiments.FindAsync(id);
        if (experiment == null) return NotFound();
        if (experiment.Status != ExperimentStatus.Running)
            return BadRequest(new { error = $"Cannot complete experiment in {experiment.Status} status" });

        experiment.Status = ExperimentStatus.Completed;
        experiment.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Experiment {Id} completed", id);
        return Ok(experiment);
    }

    /// <summary>Cancel an experiment</summary>
    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var experiment = await _db.Experiments.FindAsync(id);
        if (experiment == null) return NotFound();

        experiment.Status = ExperimentStatus.Cancelled;
        experiment.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Experiment {Id} cancelled", id);
        return Ok(experiment);
    }

    /// <summary>Get experiment results (metrics comparison between groups)</summary>
    [HttpGet("{id:int}/results")]
    public async Task<IActionResult> GetResults(int id)
    {
        var experiment = await _db.Experiments.FindAsync(id);
        if (experiment == null) return NotFound();

        return Ok(new
        {
            experiment.Id,
            experiment.Name,
            experiment.Status,
            experiment.StartedAt,
            experiment.CompletedAt,
            experiment.Winner,
            experiment.MetricsJson,
            groupA = new { config = experiment.GroupAConfig },
            groupB = new { config = experiment.GroupBConfig }
            ,analysis = ExperimentAnalyzer.Analyze(experiment.MetricsJson)
        });
    }
}

public class CreateExperimentRequest
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? GroupAConfig { get; set; }
    public string? GroupBConfig { get; set; }
    public int BotsPerGroup { get; set; } = 5;
}
