namespace BotService.Tests.NaturalnessBenchmark.Judge;

/// <summary>
/// A single scored message in the benchmark fixture.
/// </summary>
public record BenchmarkMessage(int Id, string Text, string Persona, string Category);

/// <summary>
/// Scores returned by the LLM judge for a single message, each on a 1–5 scale.
/// </summary>
public record JudgeScore(double Naturalness, double Grammar, double PersonaConsistency);
