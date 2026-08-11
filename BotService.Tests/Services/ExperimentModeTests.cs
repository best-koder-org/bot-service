using System.Text.Json;
using BotService.Services.Swarm.Modes;

namespace BotService.Tests.Services;

public class ExperimentModeTests
{
    [Fact]
    public void ParseConfig_NullJson_ReturnsDefaults()
    {
        var config = ExperimentMode.ParseConfig(null);
        Assert.Equal(5, config.Rounds);
        Assert.Equal(2000, config.DelayMs);
        Assert.Equal(0.7, config.SwipeRightProbability);
    }

    [Fact]
    public void ParseConfig_EmptyString_ReturnsDefaults()
    {
        var config = ExperimentMode.ParseConfig("");
        Assert.Equal(5, config.Rounds);
    }

    [Fact]
    public void ParseConfig_InvalidJson_ReturnsDefaults()
    {
        var config = ExperimentMode.ParseConfig("not valid json");
        Assert.Equal(5, config.Rounds);
    }

    [Fact]
    public void ParseConfig_ValidJson_ParsesValues()
    {
        var json = """{"rounds": 10, "delayMs": 500, "swipeRightProbability": 0.3}""";
        var config = ExperimentMode.ParseConfig(json);

        Assert.Equal(10, config.Rounds);
        Assert.Equal(500, config.DelayMs);
        Assert.Equal(0.3, config.SwipeRightProbability);
    }

    [Fact]
    public void ParseConfig_PartialJson_DefaultsMissing()
    {
        var json = """{"rounds": 3}""";
        var config = ExperimentMode.ParseConfig(json);

        Assert.Equal(3, config.Rounds);
        Assert.Equal(2000, config.DelayMs); // default
        Assert.Equal(0.7, config.SwipeRightProbability); // default
    }

    [Fact]
    public void ExperimentConfig_Defaults()
    {
        var config = new ExperimentConfig();
        Assert.Equal(5, config.Rounds);
        Assert.Equal(2000, config.DelayMs);
        Assert.Equal(0.7, config.SwipeRightProbability);
    }

    [Fact]
    public void ExperimentMetrics_SuccessRate_CalculatesCorrectly()
    {
        var metrics = new ExperimentMetrics { Group = "A" };
        metrics.Interactions = 100;
        metrics.Successes = 30;

        Assert.Equal(0.3, metrics.SuccessRate);
    }

    [Fact]
    public void ExperimentMetrics_SuccessRate_ZeroInteractions_ReturnsZero()
    {
        var metrics = new ExperimentMetrics { Group = "B" };
        Assert.Equal(0, metrics.SuccessRate);
    }
}
