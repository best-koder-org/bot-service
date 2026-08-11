namespace BotService.Services.Conversation;

/// <summary>
/// Detects conversation stage using message count + content signals.
/// Extends the simple PromptTemplates.DetectStage with content-aware detection:
///   - Fika mentions can accelerate to suggest_fika/post_fika stage
///   - Question density signals getting_to_know engagement
///   - Short cold replies can indicate the conversation is stalling
/// 
/// Falls back to message-count-based detection from PromptTemplates.
/// </summary>
public static class ConversationStageDetector
{
    public record StageResult(string Stage, string Reason);

    /// <summary>Detect stage from both message count and recent message content</summary>
    public static StageResult Detect(int messageCount, IReadOnlyList<string>? recentMessages = null)
    {
        // Content signals can override count-based stage
        if (recentMessages is { Count: > 0 })
        {
            var contentStage = DetectFromContent(messageCount, recentMessages);
            if (contentStage != null)
                return contentStage;
        }

        // Fall back to count-based detection (same logic as PromptTemplates.DetectStage)
        var stage = messageCount switch
        {
            <= 2 => "intro",
            <= 8 => "getting_to_know",
            <= 15 => "deep_talk",
            <= 20 => "suggest_fika",
            _ => "post_fika"
        };

        return new StageResult(stage, "message_count");
    }

    private static StageResult? DetectFromContent(int messageCount, IReadOnlyList<string> messages)
    {
        // Only analyze the last few messages for performance
        var recent = messages.Count > 5 ? messages.Skip(messages.Count - 5).ToList() : messages;
        var joined = string.Join(" ", recent).ToLowerInvariant();

        // Fika/date already planned — accelerate to post_fika
        if (messageCount >= 6 && ContainsFikaConfirmation(joined))
            return new StageResult("post_fika", "fika_confirmed");

        // Fika mentioned but not confirmed yet — suggest_fika
        if (messageCount >= 4 && ContainsFikaMention(joined))
            return new StageResult("suggest_fika", "fika_mentioned");

        // Lots of questions exchanged → deep engagement (accelerate to deep_talk)
        if (messageCount >= 5 && messageCount <= 10)
        {
            var questionCount = recent.Count(m => m.Contains('?'));
            if (questionCount >= 3)
                return new StageResult("deep_talk", "high_question_density");
        }

        return null;
    }

    private static bool ContainsFikaMention(string text) =>
        _fikaMentionKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsFikaConfirmation(string text) =>
        _fikaConfirmationKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] _fikaMentionKeywords =
    {
        "fika", "kaffe", "träffas", "ses", "dejt"
    };

    private static readonly string[] _fikaConfirmationKeywords =
    {
        "vi ses", "absolut", "ser fram emot", "vilken tid",
        "var ska vi", "bokat", "jag kommer", "perfekt"
    };
}
