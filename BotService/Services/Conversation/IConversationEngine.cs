using BotService.Models;
using BotService.Services.Llm;

namespace BotService.Services.Conversation;

/// <summary>
/// Abstraction for generating bot conversation messages.
/// Implementations: CannedConversationEngine (existing canned),
/// LlmConversationEngine (AI-powered), HybridConversationEngine (both).
/// </summary>
public interface IConversationEngine
{
    /// <summary>
    /// Generate a reply message for a bot in a conversation.
    /// </summary>
    /// <param name="context">Conversation context with persona, history, etc.</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Generated message text</returns>
    Task<ConversationReply> GenerateReplyAsync(ConversationContext context, CancellationToken ct = default);
}

/// <summary>
/// Input context for generating a conversation reply.
/// </summary>
public class ConversationContext
{
    /// <summary>The bot's persona</summary>
    public required BotPersona Persona { get; set; }
    
    /// <summary>The bot's user ID</summary>
    public required string BotUserId { get; set; }
    
    /// <summary>The matched user's ID</summary>
    public required string MatchedUserId { get; set; }
    
    /// <summary>Number of messages already exchanged in this conversation</summary>
    public int MessageCount { get; set; }
    
    /// <summary>Recent conversation messages (oldest first)</summary>
    public List<ChatMessage> RecentMessages { get; set; } = new();
}

/// <summary>
/// Result of a conversation generation attempt.
/// </summary>
public class ConversationReply
{
    /// <summary>The generated message text</summary>
    public required string Message { get; set; }
    
    /// <summary>Which engine produced the message: "llm", "canned", "fallback"</summary>
    public required string Source { get; set; }
    
    /// <summary>LLM provider name if applicable</summary>
    public string? Provider { get; set; }
    
    /// <summary>Tokens used if LLM-generated</summary>
    public int TokensUsed { get; set; }
    
    /// <summary>Latency in ms</summary>
    public long LatencyMs { get; set; }
}
