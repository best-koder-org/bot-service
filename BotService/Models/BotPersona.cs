namespace BotService.Models;

/// <summary>
/// Defines a bot persona loaded from JSON config files.
/// Each persona has an identity, profile data, and behavior configuration.
/// </summary>
public class BotPersona
{
    public string Id { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;
    public string PreferredGender { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string City { get; set; } = "Stockholm";
    public double Latitude { get; set; } = 59.3293;
    public double Longitude { get; set; } = 18.0686;
    public string Occupation { get; set; } = string.Empty;
    public string Education { get; set; } = string.Empty;
    public List<string> Interests { get; set; } = new();
    public List<string> Languages { get; set; } = new() { "Svenska", "English" };
    public string SmokingStatus { get; set; } = "Never";
    public string DrinkingStatus { get; set; } = "Socially";
    public string RelationshipType { get; set; } = "Long-term relationship";
    public int Height { get; set; } = 175;
    public string? PhotoUrl { get; set; }
    
    /// <summary>
    /// Which bot modes this persona participates in.
    /// Options: "synthetic", "warmup", "load", "shadow", "chaos"
    /// </summary>
    public List<string> Modes { get; set; } = new() { "synthetic" };
    
    /// <summary>Behavior configuration for this persona</summary>
    public BotBehavior Behavior { get; set; } = new();
}

public class BotBehavior
{
    /// <summary>Probability of swiping right (0.0 - 1.0)</summary>
    public double SwipeRightProbability { get; set; } = 0.4;
    
    /// <summary>Average delay in seconds between actions</summary>
    public int AvgActionDelaySec { get; set; } = 45;
    
    /// <summary>Min/max response delay for messages in seconds</summary>
    public int MinResponseDelaySec { get; set; } = 30;
    public int MaxResponseDelaySec { get; set; } = 300;
    
    /// <summary>Chattiness level: low, medium, high</summary>
    public string Chattiness { get; set; } = "medium";
    
    /// <summary>Active hours (UTC) — bot only acts within this window</summary>
    public int ActiveStartHourUtc { get; set; } = 7;
    public int ActiveEndHourUtc { get; set; } = 23;
    
    /// <summary>Max daily swipes (respects rate limits)</summary>
    public int MaxDailySwipes { get; set; } = 50;
    
    /// <summary>Max daily messages sent</summary>
    public int MaxDailyMessages { get; set; } = 20;
}
