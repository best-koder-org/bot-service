using BotService.Services;
using BotService.Services.Observer;
using Microsoft.AspNetCore.Mvc;

namespace BotService.Controllers;

/// <summary>
/// T380 — Prometheus metrics endpoint for bot swarm observability.
/// GET /metrics returns Prometheus text format.
/// </summary>
[ApiController]
[Route("")]
public class MetricsController : ControllerBase
{
    private readonly BotMetrics _metrics;
    private readonly BotObserver _observer;

    public MetricsController(BotMetrics metrics, BotObserver observer)
    {
        _metrics = metrics;
        _observer = observer;
    }

    /// <summary>
    /// GET /metrics — Prometheus-format metrics for bot swarms.
    /// </summary>
    [HttpGet("metrics")]
    public IActionResult GetMetrics()
    {
        // Update live gauges from observer before rendering
        var recent = _observer.GetRecentFindings(1000);
        var critical = recent.Count(f => f.Severity == Models.FindingSeverity.Critical);
        var high = recent.Count(f => f.Severity == Models.FindingSeverity.High);

        _metrics.SetGauge("bot_critical_findings", critical);
        _metrics.SetGauge("bot_high_findings", high);

        var output = _metrics.RenderPrometheus();
        return Content(output, "text/plain; charset=utf-8");
    }
}
