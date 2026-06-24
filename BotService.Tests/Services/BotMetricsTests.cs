using BotService.Services;
using Xunit;

namespace BotService.Tests.Services;

/// <summary>
/// T380 — Unit tests for BotMetrics registry + Prometheus rendering.
/// </summary>
public class BotMetricsTests
{
    [Fact]
    public void Increment_Counter_StartsAtZero()
    {
        var m = new BotMetrics();
        Assert.Equal(0, m.GetCounter("nonexistent"));
    }

    [Fact]
    public void Increment_IncreasesByOne()
    {
        var m = new BotMetrics();
        m.Increment("test_counter");
        Assert.Equal(1, m.GetCounter("test_counter"));
    }

    [Fact]
    public void Increment_ByDelta_Works()
    {
        var m = new BotMetrics();
        m.Increment("test_delta", 5);
        Assert.Equal(5, m.GetCounter("test_delta"));
    }

    [Fact]
    public void SetGauge_AndGet_ReturnsValue()
    {
        var m = new BotMetrics();
        m.SetGauge("test_gauge", 42.5);
        Assert.Equal(42.5, m.GetGauge("test_gauge"));
    }

    [Fact]
    public void Observe_Histogram_ReturnsPercentiles()
    {
        var m = new BotMetrics();
        for (var i = 1; i <= 100; i++)
            m.Observe("test_latency", i);

        var (p50, p95, p99, count) = m.GetHistogram("test_latency");
        Assert.Equal(100, count);
        Assert.True(p50 >= 49 && p50 <= 51, $"p50={p50} expected ~50");
        Assert.True(p95 >= 94 && p95 <= 96, $"p95={p95} expected ~95");
        Assert.True(p99 >= 98 && p99 <= 100, $"p99={p99} expected ~99");
    }

    [Fact]
    public void RenderPrometheus_ContainsCountersAndGauges()
    {
        var m = new BotMetrics();
        m.Increment("bot_messages_total", 10);
        m.SetGauge("bot_active_bots", 5);
        m.Observe("llm_latency_seconds", 0.5);

        var output = m.RenderPrometheus();

        Assert.Contains("bot_messages_total", output);
        Assert.Contains("bot_active_bots", output);
        Assert.Contains("llm_latency_seconds", output);
        Assert.Contains("HELP", output);
        Assert.Contains("TYPE", output);
    }

    [Fact]
    public async Task MetricsController_Returns200()
    {
        var metrics = new BotMetrics();
        metrics.Increment("bot_swipes_total", 42);

        var observerMock = new Moq.Mock<BotService.Services.Observer.BotObserver>(
            Moq.Mock.Of<System.IServiceProvider>(),
            Moq.Mock.Of<Microsoft.Extensions.Logging.ILogger<BotService.Services.Observer.BotObserver>>(),
            Moq.Mock.Of<IWebhookNotifier>());

        var controller = new BotService.Controllers.MetricsController(metrics, observerMock.Object);
        var result = controller.GetMetrics() as Microsoft.AspNetCore.Mvc.ContentResult;

        Assert.NotNull(result);
        Assert.Equal("text/plain; charset=utf-8", result.ContentType);
        Assert.Contains("bot_swipes_total 42", result.Content);
    }
}
