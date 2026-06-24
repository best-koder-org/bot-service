using BotService.Models;

namespace BotService.Services;

/// <summary>
/// T381 — Interface for webhook notification service.
/// </summary>
public interface IWebhookNotifier
{
    Task NotifyAsync(BotFinding finding, CancellationToken ct = default);
}
