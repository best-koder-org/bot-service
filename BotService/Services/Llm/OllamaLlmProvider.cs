using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BotService.Configuration;
using Microsoft.Extensions.Options;

namespace BotService.Services.Llm;

/// <summary>
/// Ollama provider — dev/local ($0, unlimited, no network dependency).
/// Uses OpenAI-compatible API at localhost:11434.
/// </summary>
public class OllamaLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaLlmProvider> _logger;
    private readonly LlmOptions _options;
    
    public string ProviderName => "ollama";

    public OllamaLlmProvider(HttpClient http, IOptions<BotServiceOptions> config, ILogger<OllamaLlmProvider> logger)
    {
        _http = http;
        _logger = logger;
        _options = config.Value.Llm;
        _http.Timeout = TimeSpan.FromSeconds(60); // Ollama can be slow
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? _options.OllamaBaseUrl;
        var model = _options.OllamaModel;
        var url = $"{baseUrl}/v1/chat/completions";

        var messages = new List<object>
        {
            new { role = "system", content = request.SystemPrompt }
        };
        foreach (var msg in request.Messages)
            messages.Add(new { role = msg.Role, content = msg.Content });
        if (request.Messages.Count == 0)
            messages.Add(new { role = "user", content = "Hej!" });

        var payload = new
        {
            model,
            messages,
            max_tokens = request.MaxTokens,
            temperature = request.Temperature,
            stream = false
        };

        var sw = Stopwatch.StartNew();
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var resp = await _http.SendAsync(httpReq, ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
                return LlmResponse.Failure(ProviderName, $"HTTP {resp.StatusCode}");

            var respJson = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(respJson);

            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            var tokensUsed = 0;
            if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                usage.TryGetProperty("total_tokens", out var total))
                tokensUsed = total.GetInt32();

            return new LlmResponse
            {
                Content = text.Trim(),
                TokensUsed = tokensUsed,
                LatencyMs = sw.ElapsedMilliseconds,
                Provider = ProviderName,
                Success = true
            };
        }
        catch (HttpRequestException)
        {
            return LlmResponse.Failure(ProviderName, "ollama_not_running");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return LlmResponse.Failure(ProviderName, "timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama API call failed");
            return LlmResponse.Failure(ProviderName, ex.Message);
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? _options.OllamaBaseUrl;
            var resp = await _http.GetAsync($"{baseUrl}/api/tags", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
