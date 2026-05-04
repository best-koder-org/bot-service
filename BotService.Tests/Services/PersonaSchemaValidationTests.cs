using System.Text.Json;
using BotService.Models;
using BotService.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotService.Tests.Services;

/// <summary>
/// Validates that all persona JSON files in BotService/Personas/ conform to the BotPersona schema.
/// </summary>
public class PersonaSchemaValidationTests
{
    private static readonly string PersonasDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Personas");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    [Fact]
    public void PersonasDirectory_Exists()
    {
        Assert.True(Directory.Exists(PersonasDir),
            $"Personas directory not found at '{PersonasDir}'. " +
            "Ensure BotService.Tests.csproj copies Personas/*.json to output.");
    }

    [Fact]
    public void PersonasDirectory_ContainsExactly50Files()
    {
        var files = Directory.GetFiles(PersonasDir, "*.json");
        Assert.Equal(50, files.Length);
    }

    [Theory]
    [MemberData(nameof(GetPersonaFiles))]
    public void PersonaFile_IsValidJson(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var exception = Record.Exception(() => JsonDocument.Parse(json));
        Assert.Null(exception);
    }

    [Theory]
    [MemberData(nameof(GetPersonaFiles))]
    public void PersonaFile_DeserializesToBotPersona(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var persona = JsonSerializer.Deserialize<BotPersona>(json, JsonOpts);

        Assert.NotNull(persona);
        Assert.False(string.IsNullOrWhiteSpace(persona.FirstName),
            $"{Path.GetFileName(filePath)}: FirstName must not be empty.");
        Assert.NotEmpty(persona.Modes);
    }

    [Theory]
    [MemberData(nameof(GetPersonaFiles))]
    public void PersonaFile_HasValidId(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var persona = JsonSerializer.Deserialize<BotPersona>(json, JsonOpts);
        Assert.NotNull(persona);

        // id may be absent; loader falls back to filename — either way it must be determinable
        var effectiveId = !string.IsNullOrEmpty(persona.Id)
            ? persona.Id
            : Path.GetFileNameWithoutExtension(filePath);

        Assert.False(string.IsNullOrWhiteSpace(effectiveId),
            $"{Path.GetFileName(filePath)}: Could not determine a valid id.");
    }

    [Theory]
    [MemberData(nameof(GetPersonaFiles))]
    public void PersonaFile_AgeInExpectedRange(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var persona = JsonSerializer.Deserialize<BotPersona>(json, JsonOpts);
        Assert.NotNull(persona);

        // Special non-standard personas (demo, chaos) may have any age
        var specialIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "demo-user", "chaos-bot" };
        if (specialIds.Contains(persona.Id ?? Path.GetFileNameWithoutExtension(filePath)))
            return;

        Assert.InRange(persona.Age, 18, 100);
    }

    [Fact]
    public void AllPersonas_LoadViaEngine()
    {
        var engine = new BotPersonaEngine(Mock.Of<ILogger<BotPersonaEngine>>());
        engine.LoadPersonas(PersonasDir);

        Assert.NotEmpty(engine.Personas);
    }

    [Fact]
    public void AgeDistribution_AtLeast5PerDecade_20To55()
    {
        var files = Directory.GetFiles(PersonasDir, "*.json");
        var specialIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "demo-user", "chaos-bot" };

        var ages = files
            .Select(f =>
            {
                var json = File.ReadAllText(f);
                return JsonSerializer.Deserialize<BotPersona>(json, JsonOpts);
            })
            .Where(p => p != null && !specialIds.Contains(p.Id ?? ""))
            .Select(p => p!.Age)
            .ToList();

        var decades = new[] { (20, 29), (30, 39), (40, 49), (50, 55) };
        foreach (var (low, high) in decades)
        {
            var count = ages.Count(a => a >= low && a <= high);
            Assert.True(count >= 5,
                $"Age range {low}–{high} has only {count} persona(s); expected at least 5.");
        }
    }

    public static IEnumerable<object[]> GetPersonaFiles()
    {
        if (!Directory.Exists(PersonasDir))
            return Enumerable.Empty<object[]>();

        return Directory.GetFiles(PersonasDir, "*.json")
            .Select(f => new object[] { f });
    }
}
