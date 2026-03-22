using BotService.Data;
using BotService.Models;
using BotService.Services.Observer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BotService.Controllers;

/// <summary>
/// REST API for querying bot findings — bugs, latency, errors discovered by bots.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class FindingsController : ControllerBase
{
    private readonly BotObserver _observer;
    private readonly BotDbContext _db;
    private readonly ILogger<FindingsController> _logger;

    public FindingsController(BotObserver observer, BotDbContext db, ILogger<FindingsController> logger)
    {
        _observer = observer;
        _db = db;
        _logger = logger;
    }

    /// <summary>Get findings summary with counts by severity/type/service</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] int? hoursBack)
    {
        var since = hoursBack.HasValue ? DateTime.UtcNow.AddHours(-hoursBack.Value) : (DateTime?)null;
        var summary = await _observer.GetSummaryAsync(since);
        return Ok(summary);
    }

    /// <summary>List recent findings with optional filters</summary>
    [HttpGet]
    public async Task<IActionResult> GetFindings(
        [FromQuery] FindingType? type,
        [FromQuery] FindingSeverity? severity,
        [FromQuery] string? service,
        [FromQuery] bool? unresolved,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var query = _db.BotFindings.AsQueryable();
        
        if (type.HasValue)
            query = query.Where(f => f.Type == type.Value);
        if (severity.HasValue)
            query = query.Where(f => f.Severity == severity.Value);
        if (!string.IsNullOrEmpty(service))
            query = query.Where(f => f.AffectedService == service);
        if (unresolved == true)
            query = query.Where(f => !f.IsResolved);
        
        var total = await query.CountAsync();
        var findings = await query
            .OrderByDescending(f => f.FoundAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        
        return Ok(new { total, page, pageSize, findings });
    }

    /// <summary>Get a specific finding by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetFinding(int id)
    {
        var finding = await _db.BotFindings.FindAsync(id);
        if (finding == null) return NotFound();
        return Ok(finding);
    }

    /// <summary>Resolve a finding (mark as addressed)</summary>
    [HttpPost("{id:int}/resolve")]
    public async Task<IActionResult> ResolveFinding(int id, [FromBody] ResolveRequest request)
    {
        var finding = await _db.BotFindings.FindAsync(id);
        if (finding == null) return NotFound();
        
        finding.IsResolved = true;
        finding.ResolutionNotes = request.Notes;
        finding.ResolvedAt = DateTime.UtcNow;
        
        await _db.SaveChangesAsync();
        _logger.LogInformation("Finding {Id} resolved: {Notes}", id, request.Notes);
        
        return Ok(finding);
    }

    /// <summary>Get recent findings from in-memory cache (fast)</summary>
    [HttpGet("recent")]
    public IActionResult GetRecent([FromQuery] int count = 20)
    {
        return Ok(_observer.GetRecentFindings(count));
    }

    /// <summary>Get LLM usage stats</summary>
    [HttpGet("llm-stats")]
    public IActionResult GetLlmStats([FromServices] BotService.Services.Llm.LlmRouter router)
    {
        var (tokensUsed, dailyBudget, primaryProvider) = router.GetUsageStats();
        return Ok(new
        {
            tokensUsedToday = tokensUsed,
            dailyBudget,
            primaryProvider,
            budgetUsedPercent = dailyBudget > 0 ? (double)tokensUsed / dailyBudget * 100 : 0
        });
    }

    /// <summary>Export findings as CSV or JSON for product analysis</summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportFindings(
        [FromQuery] string format = "json",
        [FromQuery] int? hoursBack = null,
        [FromQuery] FindingType? type = null,
        [FromQuery] FindingSeverity? severity = null)
    {
        var query = _db.BotFindings.AsQueryable();

        if (hoursBack.HasValue)
            query = query.Where(f => f.FoundAt >= DateTime.UtcNow.AddHours(-hoursBack.Value));
        if (type.HasValue)
            query = query.Where(f => f.Type == type.Value);
        if (severity.HasValue)
            query = query.Where(f => f.Severity == severity.Value);

        var findings = await query.OrderByDescending(f => f.FoundAt).ToListAsync();

        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("Id,FoundAt,Type,Severity,Title,AffectedService,BotPersona,IsResolved");
            foreach (var f in findings)
            {
                var title = f.Title?.Replace("\"", "\"\"") ?? "";
                csv.AppendLine($"{f.Id},\"{f.FoundAt:O}\",{f.Type},{f.Severity},\"{title}\",{f.AffectedService},{f.BotPersona},{f.IsResolved}");
            }
            return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv",
                $"findings-{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        return Ok(new { exportedAt = DateTime.UtcNow, count = findings.Count, findings });
    }
}

public class ResolveRequest
{
    public string Notes { get; set; } = "";
}
