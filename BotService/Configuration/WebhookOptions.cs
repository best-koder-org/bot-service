namespace BotService.Configuration;

/// <summary>
/// T381 — Webhook notification configuration for BotServiceOptions.
/// </summary>
public class WebhookOptions
{
    /// <summary>Webhook URL (Slack/Discord incoming webhook)</summary>
    public string Url { get; set; } = "";
    /// <summary>Minimum severity to notify: "critical", "high", "medium", "low"</summary>
    public string MinSeverity { get; set; } = "high";
    /// <summary>Format: "slack" or "discord"</summary>
    public string Format { get; set; } = "slack";
    /// <summary>Webhook secret for HMAC verification (if required)</summary>
    public string? Secret { get; set; }
}
