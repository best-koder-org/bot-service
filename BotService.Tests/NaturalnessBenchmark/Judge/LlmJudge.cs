using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotService.Tests.NaturalnessBenchmark.Judge;

/// <summary>
/// Scores a Swedish bot-generated message on three axes (1–5) using a configurable
/// LLM provider (Groq or Gemini). Respects a simple circuit breaker: after
/// <see cref="CircuitBreakerThreshold"/> consecutive failures the judge stops
/// sending requests and throws, preventing runaway CI costs.
/// </summary>
public sealed class LlmJudge : IDisposable
{
    private const int CircuitBreakerThreshold = 5;

    private readonly HttpClient _http;
    private readonly string _provider;
    private readonly string _apiKey;
    private int _consecutiveFailures;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private LlmJudge(string provider, string apiKey)
    {
        _provider = provider;
        _apiKey = apiKey;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Returns a judge configured from available environment variables, or
    /// <c>null</c> when no API key is present.
    /// Priority: GROQ_API_KEY → GEMINI_API_KEY.
    /// </summary>
    public static LlmJudge? TryCreate()
    {
        var groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (!string.IsNullOrWhiteSpace(groqKey))
            return new LlmJudge("groq", groqKey);

        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(geminiKey))
            return new LlmJudge("gemini", geminiKey);

        return null;
    }

    /// <summary>
    /// Scores <paramref name="message"/> on naturalness, grammar, and persona
    /// consistency. Throws <see cref="InvalidOperationException"/> when the
    /// circuit breaker is open.
    /// </summary>
    public async Task<JudgeScore> ScoreAsync(BenchmarkMessage message, CancellationToken ct = default)
    {
        if (_consecutiveFailures >= CircuitBreakerThreshold)
            throw new InvalidOperationException(
                $"LlmJudge circuit breaker open after {_consecutiveFailures} consecutive failures. " +
                "The LLM provider may be down.");

        var prompt = BuildPrompt(message);

        JudgeScore? score;
        try
        {
            score = _provider == "groq"
                ? await CallGroqAsync(prompt, ct)
                : await CallGeminiAsync(prompt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _consecutiveFailures++;
            throw;
        }

        if (score is null)
        {
            _consecutiveFailures++;
            throw new InvalidOperationException(
                $"LLM provider '{_provider}' returned an unparseable response for message id={message.Id}.");
        }

        _consecutiveFailures = 0;
        return score;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static string BuildPrompt(BenchmarkMessage message) =>
        $$"""
        You are an expert evaluator of Swedish dating-app messages.
        Score the following message on three axes, each from 1 (very poor) to 5 (excellent):
          - naturalness: sounds like a real human (not robotic or forced)
          - grammar: correct Swedish grammar and spelling
          - persona_consistency: fits the persona described below

        Persona ID: {{message.Persona}}
        Message category: {{message.Category}}
        Message: "{{message.Text}}"

        Respond ONLY with valid JSON in this exact format (integers 1-5):
        {"naturalness": N, "grammar": N, "persona_consistency": N}
        """;

    private async Task<JudgeScore?> CallGroqAsync(string prompt, CancellationToken ct)
    {
        var payload = new
        {
            model = "llama-3.3-70b-versatile",
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            max_tokens = 80,
            temperature = 0.0
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api.groq.com/openai/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return ParseScores(text);
    }

    private async Task<JudgeScore?> CallGeminiAsync(string prompt, CancellationToken ct)
    {
        var model = "gemini-1.5-flash";
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_apiKey}";

        var payload = new
        {
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } }
            },
            generationConfig = new { maxOutputTokens = 80, temperature = 0.0 }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        return ParseScores(text);
    }

    private static JudgeScore? ParseScores(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Extract the first JSON object from the response
        var match = Regex.Match(text, @"\{[^}]+\}");
        if (!match.Success) return null;

        try
        {
            using var doc = JsonDocument.Parse(match.Value);
            var root = doc.RootElement;

            if (!root.TryGetProperty("naturalness", out var n) ||
                !root.TryGetProperty("grammar", out var g) ||
                !root.TryGetProperty("persona_consistency", out var p))
                return null;

            return new JudgeScore(
                Naturalness: Clamp(n.GetDouble()),
                Grammar: Clamp(g.GetDouble()),
                PersonaConsistency: Clamp(p.GetDouble())
            );
        }
        catch
        {
            return null;
        }
    }

    private static double Clamp(double v) => Math.Clamp(v, 1.0, 5.0);
}
