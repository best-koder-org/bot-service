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
    
    /// <summary>"Tester Demo Mode" — makes bots behave like realistic fake users for human testers.</summary>
    public DemoModeOptions Demo { get; set; } = new();
    
    /// <summary>Startup delay in seconds before bots begin acting</summary>
    public int StartupDelaySec { get; set; } = 15;
    
    /// <summary>Observer/reporter settings</summary>
    public ObserverOptions Observer { get; set; } = new();
    
    /// <summary>Webhook notification config</summary>
    public WebhookOptions Webhook { get; set; } = new();

    /// <summary>Server-side voice-feedback transcription (whisper.cpp engine).</summary>
    public WhisperOptions Whisper { get; set; } = new();

    /// <summary>User feedback (voice memo) storage settings.</summary>
    public FeedbackOptions Feedback { get; set; } = new();
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
    /// <summary>YARP gateway (hosts the composite admin reset endpoints)</summary>
    public string Gateway { get; set; } = "http://localhost:8080";
    /// <summary>Internal API key for service-to-service calls (X-Internal-API-Key header)</summary>
    public string InternalApiKey { get; set; } = "";
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

/// <summary>
/// "Tester Demo Mode" — makes the app feel populated for human testers.
/// In ReactiveOnly mode bots never proactively swipe random users; they only
/// (a) reciprocate incoming human likes, (b) run targeted onboarding assist for
/// new testers, and (c) send at most one opener per human-initiated match.
/// </summary>
public class DemoModeOptions
{
    /// <summary>Master switch for demo mode. Off = legacy proactive bot behavior.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Bots never proactively swipe random users; they only like-back + onboarding assist.</summary>
    public bool ReactiveOnly { get; set; } = true;

    /// <summary>How many compatible bots pre-like a freshly signed-up tester.</summary>
    public int MaxOnboardingBots { get; set; } = 5;

    /// <summary>Send ONE opener after a human-initiated match (then strict turn-taking).</summary>
    public bool OpenerOnMatch { get; set; } = true;

    /// <summary>Auto-purge bot-flagged interactions older than this many hours (0 = disabled).</summary>
    public int PurgeTtlHours { get; set; } = 24;

    /// <summary>Purge bot-flagged interactions when demo mode is turned off.</summary>
    public bool PurgeOnStop { get; set; } = true;

    /// <summary>How often to poll for new testers to onboard (seconds).</summary>
    public int OnboardingCheckIntervalSec { get; set; } = 60;

    /// <summary>How many bots pre-like the built-in demo user on startup.</summary>
    public int PreSeedBotCount { get; set; } = 4;

    /// <summary>Also auto-reciprocate the pre-seed likes from the demo-user side (instant matches).</summary>
    public bool PreSeedAutoReciprocate { get; set; } = true;

    /// <summary>Personas that pre-like the demo user on startup.</summary>
    public List<string> PreSeedBotIds { get; set; } = new() { "astrid", "linnea", "maja", "elsa" };

    /// <summary>Max like-backs a bot performs per cycle (bounds DB volume).</summary>
    public int MaxLikeBackPerCycle { get; set; } = 5;

    /// <summary>
    /// Max number of synthetic bots that run their behavior loop (discover/like-back/chat).
    /// All personas are still provisioned (they appear in the discover feed) but only this
    /// many actually cycle — keeps the stack responsive and avoids overwhelming safety-service.
    /// 0 = unlimited (legacy).
    /// </summary>
    public int ActiveBotLimit { get; set; } = 12;
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

/// <summary>
/// Configuration for the in-process voice-feedback transcriber. The actual
/// engine is a separate lightweight whisper.cpp container (whisper-service);
/// this service just polls, uploads audio and writes transcripts back.
/// </summary>
public class WhisperOptions
{
    /// <summary>Master switch — set false to disable transcription entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL of the whisper-service (whisper.cpp server).</summary>
    public string BaseUrl { get; set; } = "http://localhost:8095";

    /// <summary>Seconds between transcription passes.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Max feedback items processed per pass.</summary>
    public int BatchSize { get; set; } = 5;

    /// <summary>Per-request HTTP timeout for a single transcription.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Language hint sent to the engine. 'auto' (default) detects per request
    /// (multilingual ggml-base handles en + sv + ...). Force 'en' or 'sv' here
    /// to lock detection to one language.
    /// </summary>
    public string Language { get; set; } = "auto";
}

/// <summary>
/// User feedback (voice memo) storage settings.
/// </summary>
public class FeedbackOptions
{
    /// <summary>
    /// Directory (relative to ContentRootPath, or absolute) where feedback audio
    /// files are persisted. In Docker this must point inside the mounted /app/data
    /// volume (e.g. /app/data/UserFeedback) so recordings survive container recreation.
    /// </summary>
    public string AudioPath { get; set; } = "Data/UserFeedback";
}
