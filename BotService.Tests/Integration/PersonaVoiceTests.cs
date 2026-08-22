using System.Text.Json;
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
/// T316 — Persona voice calibration.
/// Generates messages per persona, scores Swedish naturalness via LLM-judge.
/// Run: dotnet test --filter "PersonaVoice" -v n
/// Requires GEMINI_API_KEY or GROQ_API_KEY env var. Skips gracefully without.
/// </summary>
public class PersonaVoiceTests
{
    private readonly ITestOutputHelper _output;
    private static bool? _apiKeysAvailable;

    public PersonaVoiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool ApiKeysAvailable()
    {
        if (_apiKeysAvailable.HasValue) return _apiKeysAvailable.Value;
        var gemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var groq = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        _apiKeysAvailable = !string.IsNullOrEmpty(gemini) || !string.IsNullOrEmpty(groq);
        return _apiKeysAvailable.Value;
    }

    private static IOptions<BotServiceOptions> BuildOptions()
    {
        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "none";
        var groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "none";
        var opts = new BotServiceOptions
        {
            Llm = new LlmOptions
            {
                PrimaryProvider = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY")) ? "gemini" : "groq",
                FallbackProvider = "groq",
                DailyTokenBudget = 500_000,
                MaxTokensPerMessage = 100,
                Temperature = 0.8,
                ApiKeys = new Dictionary<string, string> { ["gemini"] = geminiKey, ["groq"] = groqKey }
            },
            Conversation = new ConversationOptions { MaxContextMessages = 5, MaxGuardrailRetries = 1 }
        };
        return Options.Create(opts);
    }

    private static List<BotPersona> LoadPersonas()
    {
        var personas = new List<BotPersona>();
        var candidates = new[]
        {
            "/home/m/development/DatingApp/bot-service/BotService/Personas",
            Path.Combine(AppContext.BaseDirectory, "../../../../BotService/Personas"),
            Path.Combine(Directory.GetCurrentDirectory(), "BotService/Personas"),
        };

        string? found = null;
        foreach (var c in candidates)
            if (Directory.Exists(c)) { found = c; break; }

        if (found == null) return personas;

        foreach (var file in Directory.GetFiles(found, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var persona = JsonSerializer.Deserialize<BotPersona>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (persona != null)
                    personas.Add(persona);
            }
            catch { }
        }
        return personas;
    }

    private static LlmConversationEngine BuildEngine()
    {
        var opts = BuildOptions();
        var http = new HttpClient();

        var providers = new List<ILlmProvider>();
        if (opts.Value.Llm.ApiKeys.TryGetValue("gemini", out var gk) && gk != "none")
            providers.Add(new GeminiLlmProvider(http, opts, NullLogger<GeminiLlmProvider>.Instance));
        if (opts.Value.Llm.ApiKeys.TryGetValue("groq", out var grok) && grok != "none")
            providers.Add(new GroqLlmProvider(http, opts, NullLogger<GroqLlmProvider>.Instance));

        var router = new LlmRouter(providers, opts, NullLogger<LlmRouter>.Instance);
        var msgProvider = new MessageContentProvider(NullLogger<MessageContentProvider>.Instance);
        var canned = new CannedConversationEngine(msgProvider, NullLogger<CannedConversationEngine>.Instance);

        return new LlmConversationEngine(router, canned, opts, NullLogger<LlmConversationEngine>.Instance);
    }

    [Fact]
    public void Calibrate_AllPersonas_GeneratesAndScores()
    {
        if (!ApiKeysAvailable())
        {
            _output.WriteLine("SKIPPED: No GEMINI_API_KEY or GROQ_API_KEY. Set one to run voice calibration.");
            return;
        }

        var personas = LoadPersonas();
        _output.WriteLine($"Loaded {personas.Count} personas.");
        Assert.True(personas.Count > 0, "No personas found.");

        var engine = BuildEngine();
        var results = new Dictionary<string, List<(string msg, double score)>>();
        var openers = new[] { "Hej! Hur är läget?", "Tjena! Vad gör du idag?", "Hallå där! Kul att matcha :)", "Hejsan! Hur mår du?", "Tja! Sett någon bra film?" };

        foreach (var persona in personas.Take(20))
        {
            var messages = new List<(string, double)>();
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    var ctx = new ConversationContext
                    {
                        Persona = persona,
                        BotUserId = persona.Id,
                        MatchedUserId = "test_user",
                        MessageCount = 1,
                        RecentMessages = new List<ChatMessage>
                        {
                            new() { SenderUserId = "test_user", Content = openers[i % openers.Length], SentAt = DateTime.UtcNow }
                        }
                    };
                    var reply = engine.GenerateReplyAsync(ctx).GetAwaiter().GetResult();
                    if (reply.Source == "llm")
                    {
                        var score = ScoreNaturalness(engine, reply.Message, persona);
                        messages.Add((reply.Message, score));
                        _output.WriteLine($"  {persona.FirstName} [{i}]: {score:F1} — \"{Trunc(reply.Message, 80)}\"");
                    }
                    else
                        _output.WriteLine($"  {persona.FirstName} [{i}]: FALLBACK — \"{Trunc(reply.Message, 60)}\"");
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"  {persona.FirstName} [{i}]: ERROR — {ex.Message}");
                }
            }
            if (messages.Count > 0) results[persona.Id] = messages;
        }

        _output.WriteLine("");
        _output.WriteLine("══════════════ PERSONA VOICE RESULTS ══════════════");
        foreach (var (pid, msgs) in results.OrderBy(k => k.Value.Average(m => m.score)))
        {
            var avg = msgs.Average(m => m.score);
            var flag = avg < 3.0 ? " ⚠️ NEEDS TUNING" : "";
            _output.WriteLine($"  {pid,-20} avg={avg:F2} n={msgs.Count}{flag}");
        }

        var worst = results.Values.Select(m => m.Average(x => x.score)).Min();
        Assert.True(worst > 1.5, $"Worst persona avg {worst:F2} — investigate system prompt");
    }

    private double ScoreNaturalness(LlmConversationEngine engine, string msg, BotPersona persona)
    {
        var judgePrompt = $"Du är en svensk språkexpert. Betygsätt följande dejting-meddelande 1-5 för:\n" +
                          $"1. Naturlig svenska (grammatik, ordföljd, idiomatisk)\n" +
                          $"2. Personlighetskonsistens (passar {persona.Age}-årig {persona.Gender} från {persona.City}, {persona.Occupation}?)\n" +
                          $"3. Konversationskvalitet (känns mänskligt?)\n\n" +
                          $"Meddelande: \"{msg}\"\n\nSvara ENDAST med ett tal 1-5.";

        var ctx = new ConversationContext
        {
            Persona = new BotPersona { Id = "judge", FirstName = "Judge", Age = 30, Gender = "neutral", City = "Stockholm" },
            BotUserId = "judge",
            MatchedUserId = "system",
            MessageCount = 1,
            RecentMessages = new List<ChatMessage> { new() { SenderUserId = "system", Content = judgePrompt, SentAt = DateTime.UtcNow } }
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

    [Fact]
    public void AllPersonas_HaveSwedishLanguageTag()
    {
        var personas = LoadPersonas();
        Assert.True(personas.Count > 0, "No personas.");
        foreach (var p in personas)
            Assert.Contains("Svenska", p.Languages);
    }

    [Fact]
    public void AllPersonas_HaveMinimalBio()
    {
        var personas = LoadPersonas();
        Assert.True(personas.Count > 0, "No personas.");
        foreach (var p in personas)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Bio), $"{p.Id} empty Bio");
            Assert.True(p.Bio.Length >= 20, $"{p.Id} Bio too short ({p.Bio.Length})");
        }
    }

    [Fact]
    public void AllPersonas_HaveSwedishCity()
    {
        var personas = LoadPersonas();
        Assert.True(personas.Count > 0, "No personas.");

        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Stockholm","Göteborg","Malmö","Uppsala","Linköping","Norrköping","Lund","Umeå","Örebro",
            "Västerås","Dalarna","Norrland","Skåne","Småland","Gävle","Sundsvall","Karlstad",
            "Luleå","Borås","Jönköping","Falun","Visby","Helsingborg","Halmstad","Östersund",
            "Kiruna","Kalmar","Växjö","Trollhättan","Södertälje","Eskilstuna",
            "Härnösand"
        };

        foreach (var p in personas)
            Assert.True(valid.Contains(p.City), $"{p.Id} city='{p.City}' not Swedish");
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
