using System.Text.Json;
using BotService.Configuration;
using BotService.Models;
using BotService.Services.Llm;
using Microsoft.Extensions.Options;

namespace BotService.Services;

/// <summary>
/// T364 — Generates bot personas on-the-fly via LLM given demographic constraints.
/// </summary>
public class DynamicPersonaGenerator
{
    private readonly LlmRouter _router;
    private readonly BotPersonaEngine _engine;
    private readonly ILogger<DynamicPersonaGenerator> _logger;

    public DynamicPersonaGenerator(
        LlmRouter router,
        BotPersonaEngine engine,
        ILogger<DynamicPersonaGenerator> logger)
    {
        _router = router;
        _engine = engine;
        _logger = logger;
    }

    public async Task<BotPersona?> GenerateAsync(
        string ageRange, string gender, string city, List<string> interests,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(ageRange, gender, city, interests);
        var request = new LlmRequest
        {
            SystemPrompt = "Du är en kreativ profilskapare för en dejtingapp. "
                + "Skapa alltid realistiska, varierade personor. "
                + "Svara ENDAST med ett JSON-objekt, ingen annan text.",
            Messages = new List<LlmMessage> { new("user", prompt) },
            MaxTokens = 500,
            Temperature = 0.9
        };

        var response = await _router.GenerateAsync(request, ct);
        if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
        {
            _logger.LogWarning("LLM failed to generate persona: {Error}", response.Error);
            return null;
        }

        try
        {
            var persona = ParsePersona(response.Content, gender, city);
            if (persona == null) return null;

            var personasDir = "/home/m/development/DatingApp/bot-service/BotService/Personas";
            Directory.CreateDirectory(personasDir);

            var filePath = Path.Combine(personasDir, $"{persona.Id}.json");
            var json = JsonSerializer.Serialize(persona, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(filePath, json, ct);
            _engine.LoadPersonas(personasDir);

            _logger.LogInformation("Generated persona {Id} ({FirstName} {LastName}, {Age})",
                persona.Id, persona.FirstName, persona.LastName, persona.Age);
            return persona;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process generated persona");
            return null;
        }
    }

    private static string BuildPrompt(string ageRange, string gender, string city, List<string> interests)
    {
        var interestList = interests.Count > 0 ? string.Join(", ", interests) : "några slumpmässiga intressen (välj själv)";
        return $"Skapa en realistisk dejtingspersona med:\n"
            + $"Ålder: {ageRange}\nKön: {gender}\nStad: {city}\nIntressen: {interestList}\n\n"
            + $"JSON-format:\n{{\n  \"id\": \"bot_{{förnamn}}-{{första bokstaven i efternamn}}\",\n"
            + "  \"firstName\": \"{förnamn}\",\n  \"lastName\": \"{efternamn}\",\n"
            + "  \"age\": {ålder},\n  \"gender\": \"{male/female}\",\n  \"city\": \"{stad}\",\n"
            + "  \"occupation\": \"{yrke}\",\n  \"education\": \"{utbildning}\",\n"
            + "  \"bio\": \"{kort biografi på svenska, max 200 tecken}\",\n"
            + "  \"interests\": [\"{intresse1}\", \"{intresse2}\", \"{intresse3}\", \"{intresse4}\"],\n"
            + "  \"languages\": [\"Svenska\"],\n  \"modes\": [\"synthetic\", \"warmup\"]\n}}\n\n"
            + $"Åldern MÅSTE vara inom {ageRange}. Staden MÅSTE vara {city}. "
            + $"Skapa en unik persona som inte liknar de andra.";
    }

    private static BotPersona? ParsePersona(string rawJson, string gender, string city)
    {
        var start = rawJson.IndexOf('{');
        var end = rawJson.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        var json = rawJson[start..(end + 1)];
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = GetStr(root, "id") ?? $"bot_generated_{Guid.NewGuid():N}";
        var firstName = GetStr(root, "firstName") ?? "Generated";
        var lastName = GetStr(root, "lastName") ?? "Persona";
        var age = root.TryGetProperty("age", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 30;
        var occupation = GetStr(root, "occupation") ?? "";
        var education = GetStr(root, "education") ?? "";
        var bio = GetStr(root, "bio") ?? "";

        var interests = new List<string>();
        if (root.TryGetProperty("interests", out var ints) && ints.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in ints.EnumerateArray())
                if (i.ValueKind == JsonValueKind.String)
                    interests.Add(i.GetString()!);
        }

        return new BotPersona
        {
            Id = id, FirstName = firstName, LastName = lastName, Age = age,
            Gender = gender, City = city, Occupation = occupation,
            Education = education, Bio = bio, Interests = interests,
            Languages = new List<string> { "Svenska" },
            Modes = new List<string> { "synthetic", "warmup" },
        };
    }

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
