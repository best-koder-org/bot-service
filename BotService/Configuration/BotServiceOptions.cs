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
    
    /// <summary>LLM provider configuration</summary>
    public LlmOptions Llm { get; set; } = new();
    
    /// <summary>Conversation engine configuration</summary>
    public ConversationOptions Conversation { get; set; } = new();
    
    /// <summary>Startup delay in seconds before bots begin acting</summary>
    public int StartupDelaySec { get; set; } = 15;
    
    /// <summary>Observer/reporter settings</summary>
    public ObserverOptions Observer { get; set; } = new();
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
    public string SafetyService { get; set; } = "http://localhost:8088";
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

/// <summary>
/// LLM provider configuration - which providers to use, models, budgets.
/// API keys should be set via environment variables (GEMINI_API_KEY, GROQ_API_KEY)
/// with appsettings as fallback.
/// </summary>
public class LlmOptions
{
    /// <summary>Primary provider name: "gemini", "groq", or "ollama"</summary>
    public string PrimaryProvider { get; set; } = "gemini";
    
    /// <summary>Fallback provider if primary is down/rate-limited</summary>
    public string FallbackProvider { get; set; } = "groq";
    
    /// <summary>Daily token budget across all providers (0 = unlimited)</summary>
    public long DailyTokenBudget { get; set; } = 500_000;
    
    /// <summary>Max tokens per single message generation</summary>
    public int MaxTokensPerMessage { get; set; } = 150;
    
    /// <summary>Temperature for generation (0.0-1.0)</summary>
    public double Temperature { get; set; } = 0.7;
    
    /// <summary>Gemini model name</summary>
    public string GeminiModel { get; set; } = "gemini-2.0-flash-lite";
    
    /// <summary>Groq model name</summary>
    public string GroqModel { get; set; } = "llama-3.3-70b-versatile";
    
    /// <summary>Ollama model name</summary>
    public string OllamaModel { get; set; } = "qwen3:8b";
    
    /// <summary>Ollama server base URL</summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    
    /// <summary>API keys fallback (prefer env vars: GEMINI_API_KEY, GROQ_API_KEY)</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new();
}

/// <summary>
/// Conversation engine configuration - controls how bots generate messages.
/// </summary>
public class ConversationOptions
{
    /// <summary>Engine mode: "llm", "canned", or "hybrid"</summary>
    public string Engine { get; set; } = "hybrid";
    
    /// <summary>In hybrid mode, use LLM after this many messages (canned for openers)</summary>
    public int HybridLlmThreshold { get; set; } = 3;
    
    /// <summary>Max conversation context messages to send to LLM</summary>
    public int MaxContextMessages { get; set; } = 20;
    
    /// <summary>Max retries if guardrails reject LLM output</summary>
    public int MaxGuardrailRetries { get; set; } = 2;
}

/// <summary>
/// Observer/reporter configuration for the bot findings system.
/// </summary>
public class ObserverOptions
{
    /// <summary>Whether the periodic reporter is enabled</summary>
    public bool ReporterEnabled { get; set; } = true;
    
    /// <summary>Hours between digest reports (default 6)</summary>
    public int ReportIntervalHours { get; set; } = 6;
}
