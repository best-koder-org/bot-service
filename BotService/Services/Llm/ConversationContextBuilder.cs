using BotService.Models;

namespace BotService.Services.Llm;

/// <summary>
/// Builds LlmMessage conversation context from API message history.
/// Truncates to fit within token budget and maintains coherence.
/// </summary>
public static class ConversationContextBuilder
{
    private const int EstimatedCharsPerToken = 4; // Conservative for Swedish text
    private const int MaxContextTokens = 2000;
    private const int MaxMessages = 20;

    /// <summary>
    /// Build a context window from raw API messages for the LLM.
    /// Messages should be in chronological order (oldest first).
    /// </summary>
    /// <param name="messages">Chat messages from API (oldest first)</param>
    /// <param name="botUserId">The bot's user ID to determine roles</param>
    /// <param name="maxTokens">Override max context tokens</param>
    /// <returns>List of LlmMessage ready for LLM request</returns>
    public static List<LlmMessage> Build(
        IEnumerable<ChatMessage> messages,
        string botUserId,
        int maxTokens = MaxContextTokens)
    {
        var result = new List<LlmMessage>();
        var totalChars = 0;
        var maxChars = maxTokens * EstimatedCharsPerToken;

        // Take most recent messages, respecting limits
        var recentMessages = messages
            .TakeLast(MaxMessages)
            .ToList();

        foreach (var msg in recentMessages)
        {
            totalChars += msg.Content?.Length ?? 0;
            if (totalChars > maxChars && result.Count > 0)
                break; // Keep at least 1 message

            result.Add(new LlmMessage
            {
                Role = msg.SenderUserId == botUserId ? "assistant" : "user",
                Content = msg.Content ?? ""
            });
        }

        return result;
    }

    /// <summary>
    /// Build context from simple string pairs (for testing or simple scenarios).
    /// </summary>
    public static List<LlmMessage> BuildFromStrings(
        IEnumerable<(string role, string content)> messages,
        int maxTokens = MaxContextTokens)
    {
        var result = new List<LlmMessage>();
        var totalChars = 0;
        var maxChars = maxTokens * EstimatedCharsPerToken;

        foreach (var (role, content) in messages)
        {
            totalChars += content.Length;
            if (totalChars > maxChars && result.Count > 0)
                break;

            result.Add(new LlmMessage { Role = role, Content = content });
        }

        return result;
    }
}

/// <summary>
/// Represents a chat message from the dating app API.
/// Maps to the message format returned by messaging-service.
/// </summary>
public class ChatMessage
{
    public string SenderUserId { get; set; } = "";
    public string RecipientUserId { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime SentAt { get; set; }
}
