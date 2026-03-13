using BotService.Services.Content;

namespace BotService.Services.Conversation;

/// <summary>
/// Conversation engine that uses pre-written Swedish messages.
/// Wraps the existing MessageContentProvider for backward compatibility.
/// Zero-cost (no API calls), deterministic, always available.
/// </summary>
public class CannedConversationEngine : IConversationEngine
{
    private readonly MessageContentProvider _messageProvider;
    private readonly ILogger<CannedConversationEngine> _logger;

    public CannedConversationEngine(
        MessageContentProvider messageProvider,
        ILogger<CannedConversationEngine> logger)
    {
        _messageProvider = messageProvider;
        _logger = logger;
    }

    public Task<ConversationReply> GenerateReplyAsync(ConversationContext context, CancellationToken ct = default)
    {
        var message = _messageProvider.GetMessageForDepth(context.MessageCount);
        
        _logger.LogDebug("Canned reply for {BotId}→{UserId} at depth {Depth}: {Msg}",
            context.BotUserId, context.MatchedUserId, context.MessageCount, 
            message.Length > 50 ? message[..50] + "..." : message);

        return Task.FromResult(new ConversationReply
        {
            Message = message,
            Source = "canned",
            Provider = null,
            TokensUsed = 0,
            LatencyMs = 0
        });
    }
}
