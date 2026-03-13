using BotService.Configuration;
using Microsoft.Extensions.Options;

namespace BotService.Services.Conversation;

/// <summary>
/// Hybrid engine: uses canned messages for early conversation (openers),
/// switches to LLM for deeper, more natural conversations.
/// This is the default engine mode.
/// </summary>
public class HybridConversationEngine : IConversationEngine
{
    private readonly CannedConversationEngine _canned;
    private readonly LlmConversationEngine _llm;
    private readonly ConversationOptions _options;
    private readonly ILogger<HybridConversationEngine> _logger;

    public HybridConversationEngine(
        CannedConversationEngine canned,
        LlmConversationEngine llm,
        IOptions<BotServiceOptions> config,
        ILogger<HybridConversationEngine> logger)
    {
        _canned = canned;
        _llm = llm;
        _options = config.Value.Conversation;
        _logger = logger;
    }

    public async Task<ConversationReply> GenerateReplyAsync(ConversationContext context, CancellationToken ct = default)
    {
        // Use canned for opening messages, LLM for deeper conversation
        if (context.MessageCount < _options.HybridLlmThreshold)
        {
            _logger.LogDebug("Hybrid: using canned for depth {Depth} (threshold={Threshold})",
                context.MessageCount, _options.HybridLlmThreshold);
            return await _canned.GenerateReplyAsync(context, ct);
        }

        _logger.LogDebug("Hybrid: using LLM for depth {Depth} (threshold={Threshold})",
            context.MessageCount, _options.HybridLlmThreshold);
        return await _llm.GenerateReplyAsync(context, ct);
    }
}
