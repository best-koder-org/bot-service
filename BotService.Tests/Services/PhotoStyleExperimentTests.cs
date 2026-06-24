using BotService.Services;
using BotService.Services.Photo;
using Xunit;

namespace BotService.Tests.Services;

/// <summary>
/// T362 — Tests for photo style A/B tracking.
/// </summary>
public class PhotoStyleExperimentTests
{
    [Fact]
    public void RecordSwipe_IncrementsCounter()
    {
        var metrics = new BotMetrics();
        var tracker = new PhotoStyleTracker(metrics);

        tracker.RecordSwipe(PhotoStyle.Casual, true);
        tracker.RecordSwipe(PhotoStyle.Casual, false);

        Assert.Equal(2, metrics.GetCounter("bot_swipes_casual_total"));
        Assert.Equal(1, metrics.GetCounter("bot_right_swipes_casual_total"));
    }

    [Fact]
    public void RecordSwipe_MatchIncrementsMatchCounter()
    {
        var metrics = new BotMetrics();
        var tracker = new PhotoStyleTracker(metrics);

        tracker.RecordSwipe(PhotoStyle.Professional, true, true);

        Assert.Equal(1, metrics.GetCounter("bot_matches_professional_total"));
    }

    [Fact]
    public void GetSuccessRate_ReturnsCorrectRatio()
    {
        var metrics = new BotMetrics();
        var tracker = new PhotoStyleTracker(metrics);

        tracker.RecordSwipe(PhotoStyle.Active, true);
        tracker.RecordSwipe(PhotoStyle.Active, true);
        tracker.RecordSwipe(PhotoStyle.Active, false);

        var rate = tracker.GetSuccessRate(PhotoStyle.Active);
        Assert.Equal(2.0 / 3.0, rate, 4);
    }

    [Fact]
    public void PickStyleForExperiment_GroupAReturnsCasualOrActive()
    {
        var style = PhotoStyleTracker.PickStyleForExperiment("A", 0);
        Assert.Contains(style, new[] { PhotoStyle.Casual, PhotoStyle.Active });
    }

    [Fact]
    public void PickStyleForExperiment_GroupBReturnsProfessional()
    {
        var style = PhotoStyleTracker.PickStyleForExperiment("B", 0);
        Assert.Equal(PhotoStyle.Professional, style);
    }

    [Fact]
    public void Summarize_ReturnsAllStyles()
    {
        var metrics = new BotMetrics();
        var tracker = new PhotoStyleTracker(metrics);
        tracker.RecordSwipe(PhotoStyle.Casual, true);

        var summary = tracker.Summarize();

        Assert.Contains("Casual", summary);
        Assert.Contains("Casual", summary);
        Assert.DoesNotContain("Professional", summary);
    }
}
