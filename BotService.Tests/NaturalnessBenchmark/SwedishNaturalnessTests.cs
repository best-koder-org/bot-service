using System.Text.Json;
using System.Text.Json.Serialization;
using BotService.Tests.NaturalnessBenchmark.Judge;

namespace BotService.Tests.NaturalnessBenchmark;

/// <summary>
/// Custom FactAttribute that skips the test when no LLM API key is configured.
/// Checks GROQ_API_KEY and GEMINI_API_KEY in that order (same env-var pattern as
/// the main BotService LLM providers).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        var groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(groqKey) && string.IsNullOrWhiteSpace(geminiKey))
            Skip = "Requires LLM API key (GROQ_API_KEY or GEMINI_API_KEY)";
    }
}

/// <summary>
/// Swedish naturalness benchmark.
/// Loads 100 representative bot-generated messages from a fixture file,
/// scores each one on three axes via an LLM judge, and asserts that the
/// mean score on every axis is ≥ 3.5.
///
/// Run with:
///   dotnet test --filter Category=Benchmark
///
/// The test is skipped (not failed) when no LLM API key is available, so it
/// never blocks normal CI runs.
/// </summary>
[Trait("Category", "Benchmark")]
public class SwedishNaturalnessTests
{
    private const double PassThreshold = 3.5;

    [BenchmarkFact]
    public async Task AllMessages_MeetNaturalnessThreshold()
    {
        // ── Load fixture ────────────────────────────────────────────────────
        var fixturesPath = Path.Combine(
            AppContext.BaseDirectory,
            "NaturalnessBenchmark", "fixtures", "messages.json");

        Assert.True(File.Exists(fixturesPath),
            $"Fixture file not found: {fixturesPath}");

        var fixtureJson = await File.ReadAllTextAsync(fixturesPath);
        var fixture = JsonSerializer.Deserialize<MessagesFixture>(fixtureJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(fixture);
        Assert.True(fixture.Messages.Count >= 100,
            $"Fixture must contain ≥100 messages, found {fixture.Messages.Count}.");

        // ── Create judge ────────────────────────────────────────────────────
        using var judge = LlmJudge.TryCreate();
        Assert.NotNull(judge); // Key is present (attribute already checked)

        // ── Score all messages ───────────────────────────────────────────────
        var scores = new List<JudgeScore>(fixture.Messages.Count);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));

        foreach (var msg in fixture.Messages)
        {
            var score = await judge.ScoreAsync(msg, cts.Token);
            scores.Add(score);
        }

        // ── Assert means ≥ 3.5 ──────────────────────────────────────────────
        var meanNaturalness = scores.Average(s => s.Naturalness);
        var meanGrammar = scores.Average(s => s.Grammar);
        var meanPersonaConsistency = scores.Average(s => s.PersonaConsistency);

        Assert.True(meanNaturalness >= PassThreshold,
            $"Mean naturalness {meanNaturalness:F2} is below threshold {PassThreshold}.");
        Assert.True(meanGrammar >= PassThreshold,
            $"Mean grammar {meanGrammar:F2} is below threshold {PassThreshold}.");
        Assert.True(meanPersonaConsistency >= PassThreshold,
            $"Mean persona consistency {meanPersonaConsistency:F2} is below threshold {PassThreshold}.");
    }

    // ── Fixture deserialization helpers ──────────────────────────────────────

    private sealed class MessagesFixture
    {
        public List<BenchmarkMessage> Messages { get; set; } = new();
    }
}
