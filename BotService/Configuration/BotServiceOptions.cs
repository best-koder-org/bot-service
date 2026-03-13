namespace BotService.Configuration;

/// <summary>
/// Root configuration for the bot service, bound from appsettings.json
/// </summary>
public class BotServiceOptions
{
    public const string SectionName = "BotService";
    
    /// <summary>Master enable switch — nothing runs if false</summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>Keycloak connection settings</summary>
    public KeycloakOptions Keycloak { get; set; } = new();
    
    /// <summary>Service endpoint URLs</summary>
    public ServiceEndpoints Endpoints { get; set; } = new();
    
    /// <summary>Per-mode enable/disable and config</summary>
    public BotModeOptions Modes { get; set; } = new();
    
    /// <summary>Startup delay in seconds before bots begin acting</summary>
    public int StartupDelaySec { get; set; } = 15;
}

public class KeycloakOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8090";
    public string Realm { get; set; } = "DatingApp";
    public string AdminUser { get; set; } = "admin";
    public string AdminPassword { get; set; } = "admin";
    public string ClientId { get; set; } = "dejtingapp-flutter";
    public string BotPasswordPrefix { get; set; } = "BotPass123!";
}

public class ServiceEndpoints
{
    public string UserService { get; set; } = "http://localhost:8082";
    public string SwipeService { get; set; } = "http://localhost:8087";
    public string MatchmakingService { get; set; } = "http://localhost:8083";
    public string MessagingService { get; set; } = "http://localhost:8086";
    public string PhotoService { get; set; } = "http://localhost:8085";
    public string MessagingHub { get; set; } = "http://localhost:8086/messagingHub";
}

public class BotModeOptions
{
    public SyntheticModeOptions Synthetic { get; set; } = new();
    public WarmupModeOptions Warmup { get; set; } = new();
    public LoadModeOptions Load { get; set; } = new();
    public ChaosModeOptions Chaos { get; set; } = new();
}

public class SyntheticModeOptions
{
    public bool Enabled { get; set; } = true;
    public int CycleIntervalSec { get; set; } = 30;
}

public class WarmupModeOptions
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalSec { get; set; } = 60;
    /// <summary>Only warmup if real user count is below this</summary>
    public int MaxRealUsersThreshold { get; set; } = 10;
}

public class LoadModeOptions
{
    public bool Enabled { get; set; } = false;
    public int MaxConcurrentBots { get; set; } = 10;
    public int TargetRequestsPerSecond { get; set; } = 5;
}

public class ChaosModeOptions
{
    public bool Enabled { get; set; } = false;
    public int CycleIntervalSec { get; set; } = 120;
    public List<string> EnabledScenarios { get; set; } = new()
    {
        "rapid-swipe", "invalid-payload", "exceed-rate-limit"
    };
}
