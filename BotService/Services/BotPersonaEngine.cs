using System.Text.Json;
using BotService.Models;

namespace BotService.Services;

/// <summary>
/// Loads and manages bot personas from JSON files in the Personas/ directory.
/// </summary>
public class BotPersonaEngine
{
    private readonly ILogger<BotPersonaEngine> _logger;
    private readonly List<BotPersona> _personas = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public BotPersonaEngine(ILogger<BotPersonaEngine> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<BotPersona> Personas => _personas.AsReadOnly();

    public void LoadPersonas(string personasDirectory)
    {
        _personas.Clear();
        
        if (!Directory.Exists(personasDirectory))
        {
            _logger.LogWarning("Personas directory not found: {Dir}", personasDirectory);
            return;
        }
        
        var files = Directory.GetFiles(personasDirectory, "*.json");
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var persona = JsonSerializer.Deserialize<BotPersona>(json, JsonOpts);
                if (persona != null)
                {
                    if (string.IsNullOrEmpty(persona.Id))
                        persona.Id = Path.GetFileNameWithoutExtension(file);
                    
                    _personas.Add(persona);
                    _logger.LogInformation("Loaded persona: {Id} ({Name}, {Gender}, modes: {Modes})",
                        persona.Id, persona.FirstName, persona.Gender, string.Join(",", persona.Modes));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load persona from {File}", file);
            }
        }
        
        _logger.LogInformation("Loaded {Count} bot personas", _personas.Count);
    }

    public List<BotPersona> GetPersonasForMode(string mode) =>
        _personas.Where(p => p.Modes.Contains(mode, StringComparer.OrdinalIgnoreCase)).ToList();

    public BotPersona? GetPersonaById(string id) =>
        _personas.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
