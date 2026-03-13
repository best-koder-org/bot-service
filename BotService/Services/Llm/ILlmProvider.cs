namespace BotService.Services.Llm;

/// <summary>
/// Provider-agnostic LLM interface. Each provider (Gemini, Groq, Ollama)
/// implements this to generate text completions.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Unique provider name for routing and logging</summary>
    string ProviderName { get; }
    
    /// <summary>Generate a text completion from messages</summary>
    Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default);
    
    /// <summary>Health check — can we reach this provider?</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

/// <summary>Request to an LLM provider</summary>
public class LlmRequest
{
    /// <summary>System prompt setting the persona/behavior</summary>
    public string SystemPrompt { get; set; } = string.Empty;
    
    /// <summary>Conversation messages in chronological order</summary>
    public List<LlmMessage> Messages { get; set; } = new();
    
    /// <summary>Max tokens to generate (keep short for chat: 100-200)</summary>
    public int MaxTokens { get; set; } = 150;
    
    /// <summary>Temperature (0.0=deterministic, 1.0=creative)</summary>
    public double Temperature { get; set; } = 0.7;
}

/// <summary>A single message in conversation context</summary>
public class LlmMessage
{
    public string Role { get; set; } = "user"; // user | assistant
    public string Content { get; set; } = string.Empty;
    
    public LlmMessage() {}
    public LlmMessage(string role, string content) { Role = role; Content = content; }
}

/// <summary>Response from an LLM provider</summary>
public class LlmResponse
{
    public string Content { get; set; } = string.Empty;
    public int TokensUsed { get; set; }
    public long LatencyMs { get; set; }
    public string Provider { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    
    public static LlmResponse Failure(string provider, string error) => new()
    {
        Provider = provider, Success = false, Error = error
    };
}
