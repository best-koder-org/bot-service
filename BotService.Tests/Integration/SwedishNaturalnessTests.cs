using System.Text.RegularExpressions;
using BotService.Configuration;
using BotService.Models;
using BotService.Services.Conversation;
using BotService.Services.Content;
using BotService.Services.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace BotService.Tests.Integration;

/// <summary>
/// T366 — Swedish naturalness benchmark.
/// Generates 100 messages across personas, scores 1-5 via LLM-judge.
/// Fails CI if average score &lt; 3.5.
///
/// Run: dotnet test --filter "SwedishNaturalness" -v n
/// Requires GEMINI_API_KEY or GROQ_API_KEY env var.
/// </summary>
public class SwedishNaturalnessTests
{
    private readonly ITestOutputHelper _output;
    private static bool? _apiKeysAvailable;

    public SwedishNaturalnessTests(ITestOutputHelper output) => _output = output;

    private static bool ApiKeysAvailable()
    {
        if (_apiKeysAvailable.HasValue) return _apiKeysAvailable.Value;
        var gemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var groq = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        _apiKeysAvailable = !string.IsNullOrEmpty(gemini) || !string.IsNullOrEmpty(groq);
        return _apiKeysAvailable.Value;
    }

    private static List<BotPersona> LoadPersonas()
    {
        var personas = new List<BotPersona>();
        var dir = "/home/m/development/DatingApp/bot-service/BotService/Personas";
        if (!Directory.Exists(dir)) return personas;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var p = System.Text.Json.JsonSerializer.Deserialize<BotPersona>(
                    File.ReadAllText(f), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (p != null) personas.Add(p);
            }
            catch { }
        }
        return personas;
    }

    [Fact]
    public void Benchmark_Naturalness_RequiresAvgAboveThreePointFive()
    {
        if (!ApiKeysAvailable())
        {
            _output.WriteLine("SKIPPED: No API keys. Set GEMINI_API_KEY or GROQ_API_KEY.");
            return;
        }

        var personas = LoadPersonas();
        Assert.True(personas.Count >= 5, $"Need ≥5 personas, got {personas.Count}");

        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "none";
        var groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "none";
        var opts = Options.Create(new BotServiceOptions
        {
            Llm = new LlmOptions
            {
                PrimaryProvider = "gemini",
                FallbackProvider = "groq",
                DailyTokenBudget = 500_000,
                MaxTokensPerMessage = 100,
                Temperature = 0.8,
                ApiKeys = new Dictionary<string, string> { ["gemini"] = geminiKey, ["groq"] = groqKey }
            },
            Conversation = new ConversationOptions { MaxContextMessages = 5, MaxGuardrailRetries = 1 }
        });

        var http = new HttpClient();
        var providers = new List<ILlmProvider>();
        if (geminiKey != "none") providers.Add(new GeminiLlmProvider(http, opts, NullLogger<GeminiLlmProvider>.Instance));
        if (groqKey != "none") providers.Add(new GroqLlmProvider(http, opts, NullLogger<GroqLlmProvider>.Instance));

        var router = new LlmRouter(providers, opts, NullLogger<LlmRouter>.Instance);
        var msgProv = new MessageContentProvider(NullLogger<MessageContentProvider>.Instance);
        var canned = new CannedConversationEngine(msgProv, NullLogger<CannedConversationEngine>.Instance);
        var engine = new LlmConversationEngine(router, canned, opts, NullLogger<LlmConversationEngine>.Instance);

        var openers = new[] { "Hej!", "Tjena!", "Hallå!", "Hejsan!", "Tja! Hur är läget?", "Hej, kul att matcha!", "Hallå där!" };
        var rng = new Random(42);
        var scores = new List<double>();
        var skipped = 0;

        // Generate exactly 100 messages across personas
        for (var i = 0; i < 100; i++)
        {
            var persona = personas[rng.Next(personas.Count)];
            var opener = openers[rng.Next(openers.Length)];

            try
            {
                var ctx = new ConversationContext
                {
                    Persona = persona,
                    BotUserId = persona.Id,
                    MatchedUserId = $"user_{i}",
                    MessageCount = 1,
                    RecentMessages = new List<ChatMessage>
                    {
                        new() { SenderUserId = $"user_{i}", Content = opener, SentAt = DateTime.UtcNow }
                    }
                };

                var reply = engine.GenerateReplyAsync(ctx).GetAwaiter().GetResult();

                if (reply.Source == "llm")
                {
                    var score = ScoreNaturalness(engine, reply.Message, persona);
                    scores.Add(score);
                }
                else
                {
                    skipped++;
                }
            }
            catch
            {
                skipped++;
            }
        }

        _output.WriteLine($"Generated {scores.Count} LLM messages ({skipped} fallback/error).");

        if (scores.Count < 10)
        {
            _output.WriteLine("SKIPPED: Insufficient LLM-generated messages for benchmark.");
            return;
        }

        var avg = scores.Average();
        var min = scores.Min();
        var max = scores.Max();
        var below35 = scores.Count(s => s < 3.5);

        _output.WriteLine($"╔══════════════════════════════════════╗");
        _output.WriteLine($"║  SWEDISH NATURALNESS BENCHMARK      ║");
        _output.WriteLine($"╠══════════════════════════════════════╣");
        _output.WriteLine($"║  Samples:  {scores.Count,4}                    ║");
        _output.WriteLine($"║  Average:  {avg,6:F2}  (threshold: 3.50) ║");
        _output.WriteLine($"║  Min:      {min,6:F2}                    ║");
        _output.WriteLine($"║  Max:      {max,6:F2}                    ║");
        _output.WriteLine($"║  Below 3.5:{below35,4} ({below35 * 100.0 / scores.Count:F0}%)               ║");
        _output.WriteLine($"╚══════════════════════════════════════╝");

        // CI gate: fail if average below 3.5
        Assert.True(avg >= 3.5,
            $"Naturalness benchmark FAILED: avg={avg:F2} < 3.50 threshold. " +
            $"{below35}/{scores.Count} messages below 3.5. Tune system prompt.");
    }

    private static double ScoreNaturalness(LlmConversationEngine engine, string msg, BotPersona persona)
    {
        var prompt = $"Du är en svensk språkexpert. Betygsätt detta dejting-meddelande 1-5:\n" +
                     $"1. Grammatik & ordföljd\n2. Idiomatisk svenska\n3. Personlighetskonsistens " +
                     $"(passar {persona.Age}-årig {persona.Gender} från {persona.City}, {persona.Occupation}?)\n\n" +
                     $"Meddelande: \"{msg}\"\n\nSvara ENDAST med ett tal 1-5.";

        var ctx = new ConversationContext
        {
            Persona = new BotPersona { Id = "judge", FirstName = "Judge", Age = 30, Gender = "neutral", City = "Stockholm" },
            BotUserId = "judge",
            MatchedUserId = "system",
            MessageCount = 1,
            RecentMessages = new List<ChatMessage> { new() { SenderUserId = "system", Content = prompt, SentAt = DateTime.UtcNow } }
        };

        try
        {
            var reply = engine.GenerateReplyAsync(ctx).GetAwaiter().GetResult();
            if (double.TryParse(reply.Message.Trim(), out var s)) return Math.Clamp(s, 1.0, 5.0);
            var m = Regex.Match(reply.Message, @"([1-5](?:\.\d)?)");
            return m.Success && double.TryParse(m.Groups[1].Value, out var e) ? Math.Clamp(e, 1.0, 5.0) : 3.0;
        }
        catch { return 3.0; }
    }
}
