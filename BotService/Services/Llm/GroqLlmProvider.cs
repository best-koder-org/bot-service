using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BotService.Configuration;
using Microsoft.Extensions.Options;

namespace BotService.Services.Llm;

/// <summary>
/// Groq provider — fallback (FREE tier: 1000 RPD, 280 tok/s, OpenAI-compatible API).
/// Uses llama-3.3-70b-versatile with auto-fallback to llama-3.1-8b-instant on 429.
/// </summary>
public class GroqLlmProvider : ILlmProvider
{
    private const string BaseUrl = "https://api.groq.com/openai/v1/chat/completions";
    private readonly HttpClient _http;
    private readonly ILogger<GroqLlmProvider> _logger;
    private readonly LlmOptions _options;
    private string _currentModel;
    
    public string ProviderName => "groq";

    public GroqLlmProvider(HttpClient http, IOptions<BotServiceOptions> config, ILogger<GroqLlmProvider> logger)
    {
        _http = http;
        _logger = logger;
        _options = config.Value.Llm;
        _currentModel = _options.GroqModel;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrEmpty(apiKey))
            return LlmResponse.Failure(ProviderName, "GROQ_API_KEY not configured");

        var response = await CallGroqAsync(request, _currentModel, apiKey, ct);
        
        // Auto-fallback to smaller model on rate limit
        if (!response.Success && response.Error == "rate_limited" && _currentModel != "llama-3.1-8b-instant")
        {
            _logger.LogWarning("Groq rate limited on {Model}, falling back to llama-3.1-8b-instant", _currentModel);
            _currentModel = "llama-3.1-8b-instant";
            response = await CallGroqAsync(request, _currentModel, apiKey, ct);
        }

        return response;
    }

    private async Task<LlmResponse> CallGroqAsync(LlmRequest request, string model, string apiKey, CancellationToken ct)
    {
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
            temperature = request.Temperature
        };

        var sw = Stopwatch.StartNew();
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var httpReq = new HttpRequestMessage(HttpMethod.Post, BaseUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var resp = await _http.SendAsync(httpReq, ct);
            sw.Stop();

            if ((int)resp.StatusCode == 429)
                return LlmResponse.Failure(ProviderName, "rate_limited");
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
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return LlmResponse.Failure(ProviderName, "timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Groq API call failed");
            return LlmResponse.Failure(ProviderName, ex.Message);
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrEmpty(apiKey)) return false;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.groq.com/openai/v1/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private string GetApiKey() =>
        Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? _options.ApiKeys.GetValueOrDefault("groq", "");
}
