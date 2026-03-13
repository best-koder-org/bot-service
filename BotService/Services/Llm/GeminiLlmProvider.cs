using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BotService.Configuration;
using Microsoft.Extensions.Options;

namespace BotService.Services.Llm;

/// <summary>
/// Google Gemini provider — primary (FREE tier: 500 RPD, excellent Swedish).
/// Uses the Gemini REST API with API key authentication.
/// </summary>
public class GeminiLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<GeminiLlmProvider> _logger;
    private readonly LlmOptions _options;
    
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    
    public string ProviderName => "gemini";

    public GeminiLlmProvider(HttpClient http, IOptions<BotServiceOptions> config, ILogger<GeminiLlmProvider> logger)
    {
        _http = http;
        _logger = logger;
        _options = config.Value.Llm;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrEmpty(apiKey))
            return LlmResponse.Failure(ProviderName, "GEMINI_API_KEY not configured");

        var model = _options.GeminiModel;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        // Build Gemini-format request
        var contents = new List<object>();
        
        // System instruction (separate field in Gemini API)
        var systemInstruction = new { parts = new[] { new { text = request.SystemPrompt } } };
        
        // Conversation messages
        foreach (var msg in request.Messages)
        {
            contents.Add(new
            {
                role = msg.Role == "assistant" ? "model" : "user",
                parts = new[] { new { text = msg.Content } }
            });
        }

        // Ensure we have at least one user message
        if (contents.Count == 0)
            contents.Add(new { role = "user", parts = new[] { new { text = "Hej!" } } });

        var payload = new
        {
            system_instruction = systemInstruction,
            contents,
            generationConfig = new
            {
                maxOutputTokens = request.MaxTokens,
                temperature = request.Temperature,
                topP = 0.95
            }
        };

        var sw = Stopwatch.StartNew();
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var response = await _http.SendAsync(httpReq, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Gemini API {Status}: {Body}", response.StatusCode, errorBody[..Math.Min(200, errorBody.Length)]);
                
                // Rate limited — signal for router to fallback
                if ((int)response.StatusCode == 429)
                    return LlmResponse.Failure(ProviderName, "rate_limited");
                    
                return LlmResponse.Failure(ProviderName, $"HTTP {response.StatusCode}");
            }

            var respJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(respJson);
            
            // Extract text from Gemini response
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty;

            // Extract token count
            var tokensUsed = 0;
            if (doc.RootElement.TryGetProperty("usageMetadata", out var usage))
            {
                if (usage.TryGetProperty("totalTokenCount", out var total))
                    tokensUsed = total.GetInt32();
            }

            _logger.LogDebug("Gemini response: {Tokens} tokens, {Ms}ms", tokensUsed, sw.ElapsedMilliseconds);
            
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
            _logger.LogError(ex, "Gemini API call failed after {Ms}ms", sw.ElapsedMilliseconds);
            return LlmResponse.Failure(ProviderName, ex.Message);
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrEmpty(apiKey)) return false;
        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}";
            var resp = await _http.GetAsync(url, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
    
    private string GetApiKey() =>
        Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? _options.ApiKeys.GetValueOrDefault("gemini", "");
}
