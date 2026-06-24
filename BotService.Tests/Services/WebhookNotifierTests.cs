using BotService.Services;
using BotService.Configuration;
using BotService.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BotService.Tests.Services;

/// <summary>
/// T381 — Unit tests for WebhookNotifier.
/// </summary>
public class WebhookNotifierTests
{
    [Fact]
    public async Task NotifyAsync_NoUrlConfigured_DoesNotThrow()
    {
        var opts = Options.Create(new BotServiceOptions { Webhook = new WebhookOptions { Url = "" } });
        var client = new HttpClient(new MockHttpHandler(200));
        var notifier = new WebhookNotifier(client, opts, NullLogger<WebhookNotifier>.Instance);

        var finding = new BotFinding { Id = 1, Title = "Test", Severity = FindingSeverity.Critical };
        await notifier.NotifyAsync(finding);

        // No exception = success
    }

    [Fact]
    public async Task NotifyAsync_LowSeverityBelowThreshold_Skips()
    {
        var opts = Options.Create(new BotServiceOptions
        {
            Webhook = new WebhookOptions { Url = "http://example.com/hook", MinSeverity = "high" }
        });
        var handler = new MockHttpHandler(200);
        var client = new HttpClient(handler);
        var notifier = new WebhookNotifier(client, opts, NullLogger<WebhookNotifier>.Instance);

        var finding = new BotFinding { Id = 2, Title = "Low", Severity = FindingSeverity.Low };
        await notifier.NotifyAsync(finding);

        Assert.False(handler.WasCalled, "Low severity should not trigger webhook when threshold is high");
    }

    [Fact]
    public async Task NotifyAsync_CriticalSeverity_SendsRequest()
    {
        var opts = Options.Create(new BotServiceOptions
        {
            Webhook = new WebhookOptions { Url = "http://example.com/hook", MinSeverity = "high" }
        });
        var handler = new MockHttpHandler(200);
        var client = new HttpClient(handler);
        var notifier = new WebhookNotifier(client, opts, NullLogger<WebhookNotifier>.Instance);

        var finding = new BotFinding { Id = 3, Title = "Critical Error", Severity = FindingSeverity.Critical };
        await notifier.NotifyAsync(finding);

        Assert.True(handler.WasCalled, "Critical severity should trigger webhook");
    }
}

/// <summary>Minimal HTTP handler that records whether it was called.</summary>
public class MockHttpHandler : DelegatingHandler
{
    private readonly int _statusCode;
    public bool WasCalled { get; private set; }

    public MockHttpHandler(int statusCode) => _statusCode = statusCode;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        WasCalled = true;
        return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)_statusCode));
    }
}
