using System.Net.Http.Json;
using System.Text.Json;
using BotService.Configuration;
using BotService.Models;
using Microsoft.Extensions.Options;

namespace BotService.Services;

/// <summary>
/// T381 — Pushes critical findings to Slack/Discord webhooks.
/// Configured via BotServiceOptions.Webhook.
/// </summary>
public class WebhookNotifier : IWebhookNotifier
{
    private readonly HttpClient _http;
    private readonly IOptions<BotServiceOptions> _options;
    private readonly ILogger<WebhookNotifier> _logger;

    public WebhookNotifier(
        HttpClient http,
        IOptions<BotServiceOptions> options,
        ILogger<WebhookNotifier> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Send a finding to configured webhooks if its severity meets the threshold.
    /// </summary>
    public async Task NotifyAsync(BotFinding finding, CancellationToken ct = default)
    {
        var cfg = _options.Value.Webhook;
        if (cfg == null || string.IsNullOrEmpty(cfg.Url)) return;

        // Respect severity threshold
        var threshold = cfg.MinSeverity?.ToLowerInvariant() ?? "high";
        if (!MeetsThreshold(finding.Severity.ToString().ToLowerInvariant(), threshold))
            return;

        try
        {
            var payload = BuildPayload(finding);

            _logger.LogInformation("Sending webhook notification for finding {Title} ({Severity})",
                finding.Title, finding.Severity);

            var response = await _http.PostAsJsonAsync(cfg.Url, payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Webhook returned {Status} for finding {Title}",
                    response.StatusCode, finding.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook send failed for finding {Title}", finding.Title);
        }
    }

    private object BuildPayload(BotFinding finding)
    {
        var cfg = _options.Value.Webhook!;
        var color = finding.Severity switch
        {
            FindingSeverity.Critical => 0xFF0000,
            FindingSeverity.High => 0xFFA500,
            FindingSeverity.Medium => 0xFFFF00,
            _ => 0x808080,
        };

        var (emoji, severityLabel) = finding.Severity switch
        {
            FindingSeverity.Critical => ("🔴", "Critical"),
            FindingSeverity.High => ("🟠", "High"),
            FindingSeverity.Medium => ("🟡", "Medium"),
            _ => ("⚪", "Low"),
        };

        var description = string.IsNullOrEmpty(finding.Description)
            ? finding.Title
            : finding.Description;
        var descriptionTruncated = description.Length > 500
            ? description[..497] + "..."
            : description;

        // Slack-compatible format (also works with Discord)
        return new
        {
            username = "Bot Swarm",
            icon_emoji = ":robot_face:",
            attachments = new[]
            {
                new
                {
                    color = $"#{color:X6}",
                    title = $"{emoji} {severityLabel}: {finding.Title}",
                    fields = new[]
                    {
                        new { title = "Type", value = finding.Type.ToString(), @short = true },
                        new { title = "Severity", value = severityLabel, @short = true },
                        new { title = "Description", value = descriptionTruncated, @short = false },
                    },
                    footer = cfg.Format == "discord"
                        ? "Bot Swarm Monitor"
                        : "Bot Swarm Monitor",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                }
            }
        };
    }

    private static bool MeetsThreshold(string severity, string threshold)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = 0,
            ["high"] = 1,
            ["medium"] = 2,
            ["low"] = 3,
        };

        var s = order.GetValueOrDefault(severity, 3);
        var t = order.GetValueOrDefault(threshold, 1);
        return s <= t; // lower number = more severe
    }
}

/// <summary>
/// Webhook configuration section for BotServiceOptions.
/// </summary>
