using System.Net.Http.Headers;
using System.Text.Json;
using BotService.Configuration;
using Microsoft.Extensions.Options;

namespace BotService.Services.Whisper;

/// <summary>
/// Typed HttpClient for the whisper-service (whisper.cpp server).
/// POSTs an audio file to /inference and parses the {"text": "..."} response.
/// </summary>
public class WhisperApiClient : IWhisperApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<WhisperApiClient> _logger;
    private readonly WhisperOptions _options;

    public WhisperApiClient(
        HttpClient http,
        IOptions<BotServiceOptions> config,
        ILogger<WhisperApiClient> logger)
    {
        _http = http;
        _logger = logger;
        _options = config.Value.Whisper;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds));
    }

    public async Task<string?> TranscribeAsync(Stream audio, string fileName, CancellationToken ct)
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');

        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(audio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", fileName);
        content.Add(new StringContent("0.0"), "temperature");
        content.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(_options.Language) &&
            !string.Equals(_options.Language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            // Only pin the language when explicitly configured; 'auto' lets the
            // server detect per request (multilingual model).
            content.Add(new StringContent(_options.Language), "language");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/inference")
        {
            Content = content
        };

        try
        {
            var resp = await _http.SendAsync(request, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("whisper-service returned {Status}: {Body}",
                    resp.StatusCode, Truncate(body, 300));
                throw new WhisperEngineUnavailableException($"HTTP {(int)resp.StatusCode}");
            }

            if (string.IsNullOrWhiteSpace(body)) return null;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("text", out var textProp))
            {
                var text = textProp.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            return body.Trim();
        }
        catch (WhisperEngineUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("whisper-service transcription timed out after {Timeout}s",
                _http.Timeout.TotalSeconds);
            throw new WhisperEngineUnavailableException("timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "whisper-service unreachable at {BaseUrl}", _options.BaseUrl);
            throw new WhisperEngineUnavailableException($"unreachable: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "whisper-service returned non-JSON response");
            return null; // audio produced nothing parseable → treat as unreadable
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "whisper-service transcription failed");
            throw new WhisperEngineUnavailableException(ex.Message);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
