using BotService.Configuration;
using BotService.Services.Llm;
using Microsoft.Extensions.Options;

namespace BotService.Services.Conversation;

/// <summary>
/// AI-powered conversation engine using LLM providers.
/// Generates persona-aware Swedish messages with guardrails and fallback.
/// </summary>
public class LlmConversationEngine : IConversationEngine
{
    private readonly LlmRouter _router;
    private readonly CannedConversationEngine _cannedFallback;
    private readonly ConversationOptions _convOptions;
    private readonly LlmOptions _llmOptions;
    private readonly ILogger<LlmConversationEngine> _logger;

    public LlmConversationEngine(
        LlmRouter router,
        CannedConversationEngine cannedFallback,
        IOptions<BotServiceOptions> config,
        ILogger<LlmConversationEngine> logger)
    {
        _router = router;
        _cannedFallback = cannedFallback;
        _convOptions = config.Value.Conversation;
        _llmOptions = config.Value.Llm;
        _logger = logger;
    }

    public async Task<ConversationReply> GenerateReplyAsync(ConversationContext context, CancellationToken ct = default)
    {
        // Detect conversation stage from message count
        var stage = PromptTemplates.DetectStage(context.MessageCount);
        var systemPrompt = PromptTemplates.BuildSystemPrompt(context.Persona, stage);

        // Build context window from message history
        var llmMessages = ConversationContextBuilder.Build(
            context.RecentMessages, context.BotUserId, _convOptions.MaxContextMessages * 100);

        var request = new LlmRequest
        {
            SystemPrompt = systemPrompt,
            Messages = llmMessages,
            MaxTokens = _llmOptions.MaxTokensPerMessage,
            Temperature = _llmOptions.Temperature
        };

        // Try LLM with guardrail retries
        for (var attempt = 0; attempt <= _convOptions.MaxGuardrailRetries; attempt++)
        {
            var response = await _router.GenerateAsync(request, ct);
            
            if (!response.Success)
            {
                _logger.LogWarning("LLM generation failed (attempt {Attempt}): {Error}", 
                    attempt + 1, response.Error);
                break; // No point retrying if provider is down
            }

            // Run guardrails
            var rejection = ResponseGuardrails.Validate(response.Content);
            if (rejection == null)
            {
                _logger.LogInformation(
                    "LLM reply generated for {BotId}→{UserId} via {Provider} (stage={Stage}, tokens={Tokens}, {Ms}ms)",
                    context.BotUserId, context.MatchedUserId, response.Provider,
                    stage, response.TokensUsed, response.LatencyMs);

                return new ConversationReply
                {
                    Message = response.Content,
                    Source = "llm",
                    Provider = response.Provider,
                    TokensUsed = response.TokensUsed,
                    LatencyMs = response.LatencyMs
                };
            }

            _logger.LogWarning("Guardrail rejected LLM output (attempt {Attempt}, reason={Reason}): {Content}",
                attempt + 1, rejection, response.Content.Length > 80 ? response.Content[..80] + "..." : response.Content);
        }

        // All LLM attempts failed — fall back to canned
        _logger.LogWarning("LLM exhausted, falling back to canned for {BotId}→{UserId}",
            context.BotUserId, context.MatchedUserId);
        
        var fallback = await _cannedFallback.GenerateReplyAsync(context, ct);
        fallback.Source = "llm_fallback_canned"; return fallback;
    }
}
