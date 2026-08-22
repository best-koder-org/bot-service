using System.Net;
using System.Text;
using BotService.Configuration;
using BotService.Services.Whisper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace BotService.Tests.Services;

public class WhisperApiClientTests
{
    private static WhisperApiClient CreateClient(
        HttpMessageHandler handler,
        string baseUrl = "http://whisper:8095")
    {
        var http = new HttpClient(handler);
        var options = Options.Create(new BotServiceOptions
        {
            Whisper = new WhisperOptions { BaseUrl = baseUrl }
        });
        return new WhisperApiClient(http, options, NullLogger<WhisperApiClient>.Instance);
    }

    private static HttpMessageHandler MockHandler(HttpStatusCode status, string body)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = new StringContent(body)
            });
        return handler.Object;
    }

    [Fact]
    public async Task TranscribeAsync_ReturnsText_FromJsonResponse()
    {
        var client = CreateClient(MockHandler(HttpStatusCode.OK, """{"text":"Hej där!"}"""));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake-audio-bytes"));

        var result = await client.TranscribeAsync(stream, "clip.m4a", CancellationToken.None);

        Assert.Equal("Hej där!", result);
    }

    [Fact]
    public async Task TranscribeAsync_ReturnsNull_WhenTextIsEmpty()
    {
        var client = CreateClient(MockHandler(HttpStatusCode.OK, """{"text":""}"""));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake"));

        Assert.Null(await client.TranscribeAsync(stream, "clip.m4a", CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_ThrowsEngineUnavailable_OnServerError()
    {
        var client = CreateClient(MockHandler(HttpStatusCode.InternalServerError, "boom"));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake"));

        await Assert.ThrowsAsync<WhisperEngineUnavailableException>(
            () => client.TranscribeAsync(stream, "clip.m4a", CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_ThrowsEngineUnavailable_WhenUnreachable()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var client = CreateClient(handler.Object);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake"));

        await Assert.ThrowsAsync<WhisperEngineUnavailableException>(
            () => client.TranscribeAsync(stream, "clip.m4a", CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_ReturnsNull_OnNonJsonBody()
    {
        var client = CreateClient(MockHandler(HttpStatusCode.OK, "just plain text"));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake"));

        Assert.Null(await client.TranscribeAsync(stream, "clip.m4a", CancellationToken.None));
    }

    private static (WhisperApiClient client, CaptureHandler handler) CreateCapturingClient(
        string? language = null)
    {
        var handler = new CaptureHandler();
        var http = new HttpClient(handler);
        var options = Options.Create(new BotServiceOptions
        {
            Whisper = new WhisperOptions
            {
                BaseUrl = "http://whisper:8095",
                Language = language ?? "auto"
            }
        });
        return (new WhisperApiClient(http, options, NullLogger<WhisperApiClient>.Instance), handler);
    }

    /// <summary>Real HttpMessageHandler that captures the outgoing request.</summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"Hej"}""")
            };
        }
    }

    [Fact]
    public async Task TranscribeAsync_SendsLanguageField_WhenConfigured()
    {
        var (client, handler) = CreateCapturingClient("sv");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake"));

        var result = await client.TranscribeAsync(stream, "clip.m4a", CancellationToken.None);

        Assert.Equal("Hej", result);
        Assert.NotNull(handler.LastBody);
        Assert.Contains("language", handler.LastBody);
        Assert.Contains("sv", handler.LastBody);
    }

    [Fact]
    public async Task TranscribeAsync_OmitsLanguageField_WhenAuto()
    {
        var (client, handler) = CreateCapturingClient(); // default auto
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake"));

        await client.TranscribeAsync(stream, "clip.m4a", CancellationToken.None);

        Assert.NotNull(handler.LastBody);
        Assert.DoesNotContain("language", handler.LastBody);
    }
}
