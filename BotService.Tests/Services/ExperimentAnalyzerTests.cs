using BotService.Services.Swarm;

namespace BotService.Tests.Services;

public class ExperimentAnalyzerTests
{
    [Fact]
    public void Analyze_NullMetrics_ReturnsSummary()
    {
        var result = ExperimentAnalyzer.Analyze(null);
        Assert.Equal("No metrics data", result.Summary);
        Assert.Equal("none", result.Winner);
        Assert.False(result.IsSignificant);
    }

    [Fact]
    public void Analyze_EmptyMetrics_ReturnsSummary()
    {
        var result = ExperimentAnalyzer.Analyze("");
        Assert.Equal("No metrics data", result.Summary);
    }

    [Fact]
    public void Analyze_InvalidJson_ReturnsError()
    {
        var result = ExperimentAnalyzer.Analyze("not json");
        Assert.Contains("Analysis error", result.Summary);
    }

    [Fact]
    public void Analyze_EqualRates_NotSignificant()
    {
        var json = """{"groupA":{"interactions":100,"successes":50},"groupB":{"interactions":100,"successes":50}}""";
        var result = ExperimentAnalyzer.Analyze(json);

        Assert.Equal("none", result.Winner);
        Assert.False(result.IsSignificant);
        Assert.Equal(0.5, result.GroupARate);
        Assert.Equal(0.5, result.GroupBRate);
    }

    [Fact]
    public void Analyze_LargeDifference_Significant()
    {
        // A: 80% success, B: 20% success with 100 samples each should be significant
        var json = """{"groupA":{"interactions":100,"successes":80},"groupB":{"interactions":100,"successes":20}}""";
        var result = ExperimentAnalyzer.Analyze(json);

        Assert.True(result.IsSignificant);
        Assert.Equal("A", result.Winner);
        Assert.Equal(0.8, result.GroupARate);
        Assert.Equal(0.2, result.GroupBRate);
        Assert.True(result.PValue < 0.05);
    }

    [Fact]
    public void Analyze_BWins_ReturnsB()
    {
        var json = """{"groupA":{"interactions":100,"successes":10},"groupB":{"interactions":100,"successes":90}}""";
        var result = ExperimentAnalyzer.Analyze(json);

        Assert.True(result.IsSignificant);
        Assert.Equal("B", result.Winner);
    }

    [Fact]
    public void Analyze_SmallSample_NotAdequate()
    {
        var json = """{"groupA":{"interactions":10,"successes":5},"groupB":{"interactions":10,"successes":3}}""";
        var result = ExperimentAnalyzer.Analyze(json);

        Assert.False(result.SampleSizeAdequate);
    }

    [Fact]
    public void Analyze_LargeSample_Adequate()
    {
        var json = """{"groupA":{"interactions":50,"successes":25},"groupB":{"interactions":50,"successes":20}}""";
        var result = ExperimentAnalyzer.Analyze(json);

        Assert.True(result.SampleSizeAdequate);
    }

    [Fact]
    public void Analyze_ZeroInteractions_HandlesGracefully()
    {
        var json = """{"groupA":{"interactions":0,"successes":0},"groupB":{"interactions":0,"successes":0}}""";
        var result = ExperimentAnalyzer.Analyze(json);

        Assert.Equal("none", result.Winner);
        Assert.Equal(0, result.GroupARate);
        Assert.Equal(0, result.GroupBRate);
    }

    [Fact]
    public void ComputeTStatistic_EqualProportions_ReturnsZero()
    {
        var t = ExperimentAnalyzer.ComputeTStatistic(0.5, 0.5, 100, 100);
        Assert.Equal(0, t);
    }

    [Fact]
    public void ComputeTStatistic_DifferentProportions_ReturnsNonZero()
    {
        var t = ExperimentAnalyzer.ComputeTStatistic(0.8, 0.2, 100, 100);
        Assert.True(Math.Abs(t) > 2); // Large effect = large t
    }

    [Fact]
    public void ComputeTStatistic_ZeroN_ReturnsZero()
    {
        var t = ExperimentAnalyzer.ComputeTStatistic(0.5, 0.5, 0, 100);
        Assert.Equal(0, t);
    }

    [Fact]
    public void ComputeDegreesOfFreedom_ReasonableValues()
    {
        var df = ExperimentAnalyzer.ComputeDegreesOfFreedom(0.5, 0.3, 100, 100);
        Assert.True(df > 0);
    }

    [Fact]
    public void ComputeDegreesOfFreedom_SmallN_ReturnsPositive()
    {
        var df = ExperimentAnalyzer.ComputeDegreesOfFreedom(0.5, 0.5, 2, 2);
        Assert.True(df > 0);
    }

    [Fact]
    public void NormalCdf_AtZero_ReturnsHalf()
    {
        var result = ExperimentAnalyzer.NormalCdf(0);
        Assert.Equal(0.5, result, 4);
    }

    [Fact]
    public void NormalCdf_LargePositive_ReturnsNearOne()
    {
        var result = ExperimentAnalyzer.NormalCdf(5);
        Assert.True(result > 0.999);
    }

    [Fact]
    public void NormalCdf_LargeNegative_ReturnsNearZero()
    {
        var result = ExperimentAnalyzer.NormalCdf(-5);
        Assert.True(result < 0.001);
    }

    [Fact]
    public void ApproximatePValue_ZeroT_ReturnsOne()
    {
        var p = ExperimentAnalyzer.ApproximatePValue(0, 100);
        Assert.Equal(1.0, p, 2);
    }

    [Fact]
    public void ApproximatePValue_LargeT_ReturnsSmall()
    {
        var p = ExperimentAnalyzer.ApproximatePValue(5.0, 100);
        Assert.True(p < 0.001);
    }
}
