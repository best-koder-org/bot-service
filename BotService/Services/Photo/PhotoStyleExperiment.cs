using BotService.Models;

namespace BotService.Services.Photo;

/// <summary>
/// T362 — Photo style classification for A/B testing.
/// Tracks which photo styles generate more right-swipes across experiment groups.
/// </summary>
public enum PhotoStyle
{
    Casual = 0,
    Professional = 1,
    Active = 2,
    Default = 3,
}

/// <summary>
/// T362 — Tracks swipe results by photo style and reports to BotMetrics.
/// </summary>
public class PhotoStyleTracker
{
    private readonly BotMetrics _metrics;

    public PhotoStyleTracker(BotMetrics metrics)
    {
        _metrics = metrics;
    }

    /// <summary>Record a swipe outcome for a given photo style.</summary>
    public void RecordSwipe(PhotoStyle style, bool wasRightSwipe, bool resultedInMatch = false)
    {
        var styleName = style.ToString().ToLowerInvariant();

        _metrics.Increment($"bot_swipes_{styleName}_total");
        if (wasRightSwipe)
            _metrics.Increment($"bot_right_swipes_{styleName}_total");
        if (resultedInMatch)
            _metrics.Increment($"bot_matches_{styleName}_total");
    }

    /// <summary>Get swipe success rate for a photo style from metrics.</summary>
    public double GetSuccessRate(PhotoStyle style)
    {
        var styleName = style.ToString().ToLowerInvariant();
        var total = _metrics.GetCounter($"bot_swipes_{styleName}_total");
        var right = _metrics.GetCounter($"bot_right_swipes_{styleName}_total");
        return total > 0 ? (double)right / total : 0;
    }

    /// <summary>Assign an appropriate photo style based on experiment group.</summary>
    public static PhotoStyle PickStyleForExperiment(string group, int seed)
    {
        if (group == "A") return (PhotoStyle)(seed % 2 == 0 ? 0 : 2); // Casual or Active
        if (group == "B") return PhotoStyle.Professional;              // Professional
        return PhotoStyle.Default;
    }

    /// <summary>Summarize photo A/B test results.</summary>
    public string Summarize()
    {
        var sb = new System.Text.StringBuilder();
        foreach (PhotoStyle style in Enum.GetValues<PhotoStyle>())
        {
            var total = _metrics.GetCounter($"bot_swipes_{style.ToString().ToLowerInvariant()}_total");
            var success = GetSuccessRate(style);
            if (total > 0)
                sb.AppendLine($"  {style}: {total} swipes, {success:P1} right-swipe rate");
        }
        return sb.ToString();
    }
}
