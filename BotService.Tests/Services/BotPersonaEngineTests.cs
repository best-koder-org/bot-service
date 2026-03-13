using BotService.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotService.Tests.Services;

public class BotPersonaEngineTests : IDisposable
{
    private readonly BotPersonaEngine _engine;
    private readonly string _tempDir;

    public BotPersonaEngineTests()
    {
        _engine = new BotPersonaEngine(Mock.Of<ILogger<BotPersonaEngine>>());
        _tempDir = Path.Combine(Path.GetTempPath(), $"bot-personas-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void LoadPersonas_WithValidJson_LoadsCorrectly()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test-bot.json"), """
        {
          "id": "test-bot",
          "firstName": "Test",
          "lastName": "Bot",
          "age": 25,
          "gender": "female",
          "preferredGender": "male",
          "bio": "Test bio",
          "city": "Stockholm",
          "modes": ["synthetic", "warmup"]
        }
        """);

        _engine.LoadPersonas(_tempDir);

        Assert.Single(_engine.Personas);
        Assert.Equal("test-bot", _engine.Personas[0].Id);
        Assert.Equal("Test", _engine.Personas[0].FirstName);
        Assert.Equal(25, _engine.Personas[0].Age);
        Assert.Contains("synthetic", _engine.Personas[0].Modes);
    }

    [Fact]
    public void LoadPersonas_WithMissingDirectory_LoadsNothing()
    {
        _engine.LoadPersonas("/nonexistent/path");
        Assert.Empty(_engine.Personas);
    }

    [Fact]
    public void LoadPersonas_MultileFiles_LoadsAll()
    {
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_tempDir, $"bot-{i}.json"), $$"""
            {
              "id": "bot-{{i}}",
              "firstName": "Bot{{i}}",
              "lastName": "Test",
              "age": 25,
              "gender": "male",
              "modes": ["synthetic"]
            }
            """);
        }

        _engine.LoadPersonas(_tempDir);
        Assert.Equal(5, _engine.Personas.Count);
    }

    [Fact]
    public void GetPersonasForMode_ReturnsOnlyMatchingMode()
    {
        File.WriteAllText(Path.Combine(_tempDir, "synthetic-bot.json"), """
        { "id": "s1", "firstName": "S", "modes": ["synthetic"] }
        """);
        File.WriteAllText(Path.Combine(_tempDir, "warmup-bot.json"), """
        { "id": "w1", "firstName": "W", "modes": ["warmup"] }
        """);
        File.WriteAllText(Path.Combine(_tempDir, "both-bot.json"), """
        { "id": "b1", "firstName": "B", "modes": ["synthetic", "warmup"] }
        """);

        _engine.LoadPersonas(_tempDir);

        var synthetic = _engine.GetPersonasForMode("synthetic");
        Assert.Equal(2, synthetic.Count);

        var warmup = _engine.GetPersonasForMode("warmup");
        Assert.Equal(2, warmup.Count);

        var chaos = _engine.GetPersonasForMode("chaos");
        Assert.Empty(chaos);
    }

    [Fact]
    public void GetPersonaById_ReturnsCorrectPersona()
    {
        File.WriteAllText(Path.Combine(_tempDir, "astrid.json"), """
        { "id": "astrid", "firstName": "Astrid", "modes": ["synthetic"] }
        """);

        _engine.LoadPersonas(_tempDir);

        var found = _engine.GetPersonaById("astrid");
        Assert.NotNull(found);
        Assert.Equal("Astrid", found.FirstName);

        var notFound = _engine.GetPersonaById("nonexistent");
        Assert.Null(notFound);
    }

    [Fact]
    public void GetPersonaById_IsCaseInsensitive()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.json"), """
        { "id": "Test-Bot", "firstName": "Test", "modes": [] }
        """);

        _engine.LoadPersonas(_tempDir);

        Assert.NotNull(_engine.GetPersonaById("test-bot"));
        Assert.NotNull(_engine.GetPersonaById("TEST-BOT"));
    }

    [Fact]
    public void LoadPersonas_FallsBackToFilename_WhenIdMissing()
    {
        File.WriteAllText(Path.Combine(_tempDir, "my-persona.json"), """
        { "firstName": "NoId", "modes": ["synthetic"] }
        """);

        _engine.LoadPersonas(_tempDir);

        Assert.Single(_engine.Personas);
        Assert.Equal("my-persona", _engine.Personas[0].Id);
    }
}
