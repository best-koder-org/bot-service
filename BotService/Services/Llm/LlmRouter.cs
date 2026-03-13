using BotService.Configuration;
using Microsoft.Extensions.Options;

namespace BotService.Services.Llm;

/// <summary>
/// Routes LLM requests to the best available provider.
/// Implements circuit breaker (3 failures → fallback), token budget tracking,
/// and provider priority ordering.
/// </summary>
public class LlmRouter
{
    private readonly Dictionary<string, ILlmProvider> _providers;
    private readonly ILogger<LlmRouter> _logger;
    private readonly LlmOptions _options;
    
    // Circuit breaker state per provider
    private readonly Dictionary<string, int> _failureCounts = new();
    private readonly Dictionary<string, DateTime> _circuitOpenUntil = new();
    private const int CircuitBreakerThreshold = 3;
    private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromMinutes(5);
    
    // Daily token budget tracking
    private long _tokensUsedToday;
    private DateTime _budgetResetDate = DateTime.UtcNow.Date;
    private readonly object _budgetLock = new();

    public LlmRouter(
        IEnumerable<ILlmProvider> providers,
        IOptions<BotServiceOptions> config,
        ILogger<LlmRouter> logger)
    {
        _providers = providers.ToDictionary(p => p.ProviderName);
        _logger = logger;
        _options = config.Value.Llm;
    }

    /// <summary>Route a request through available providers with fallback chain</summary>
    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        ResetBudgetIfNewDay();
        
        // Check if budget exhausted
        if (_options.DailyTokenBudget > 0 && _tokensUsedToday >= _options.DailyTokenBudget)
        {
            _logger.LogWarning("Daily token budget exhausted ({Used}/{Budget})", _tokensUsedToday, _options.DailyTokenBudget);
            return LlmResponse.Failure("router", "budget_exhausted");
        }

        // Try providers in priority order
        var providerOrder = GetProviderOrder();
        
        foreach (var providerName in providerOrder)
        {
            if (!_providers.TryGetValue(providerName, out var provider)) continue;
            if (IsCircuitOpen(providerName)) continue;

            var response = await provider.GenerateAsync(request, ct);
            
            if (response.Success)
            {
                ResetCircuit(providerName);
                TrackTokens(response.TokensUsed);
                _logger.LogDebug("LLM routed to {Provider}: {Tokens} tokens, {Ms}ms",
                    providerName, response.TokensUsed, response.LatencyMs);
                return response;
            }
            
            RecordFailure(providerName, response.Error ?? "unknown");
        }

        _logger.LogError("All LLM providers failed");
        return LlmResponse.Failure("router", "all_providers_failed");
    }

    /// <summary>Get current token usage stats</summary>
    public (long used, long budget, string primaryProvider) GetUsageStats()
    {
        ResetBudgetIfNewDay();
        return (_tokensUsedToday, _options.DailyTokenBudget, _options.PrimaryProvider);
    }

    private List<string> GetProviderOrder()
    {
        var order = new List<string>();
        if (!string.IsNullOrEmpty(_options.PrimaryProvider))
            order.Add(_options.PrimaryProvider);
        if (!string.IsNullOrEmpty(_options.FallbackProvider) && _options.FallbackProvider != _options.PrimaryProvider)
            order.Add(_options.FallbackProvider);
        
        // Add any remaining registered providers
        foreach (var name in _providers.Keys)
            if (!order.Contains(name)) order.Add(name);
        
        return order;
    }

    private bool IsCircuitOpen(string provider)
    {
        lock (_budgetLock)
        {
            if (_circuitOpenUntil.TryGetValue(provider, out var until) && DateTime.UtcNow < until)
            {
                _logger.LogDebug("Circuit open for {Provider} until {Until}", provider, until);
                return true;
            }
            return false;
        }
    }

    private void RecordFailure(string provider, string error)
    {
        lock (_budgetLock)
        {
            _failureCounts[provider] = _failureCounts.GetValueOrDefault(provider) + 1;
            if (_failureCounts[provider] >= CircuitBreakerThreshold)
            {
                _circuitOpenUntil[provider] = DateTime.UtcNow + CircuitOpenDuration;
                _logger.LogWarning("Circuit breaker OPEN for {Provider} ({Error}), {Min}min cooldown",
                    provider, error, CircuitOpenDuration.TotalMinutes);
            }
        }
    }

    private void ResetCircuit(string provider)
    {
        lock (_budgetLock)
        {
            _failureCounts[provider] = 0;
            _circuitOpenUntil.Remove(provider);
        }
    }

    private void TrackTokens(int tokens)
    {
        lock (_budgetLock)
        {
            _tokensUsedToday += tokens;
        }
    }

    private void ResetBudgetIfNewDay()
    {
        var today = DateTime.UtcNow.Date;
        if (_budgetResetDate < today)
        {
            lock (_budgetLock)
            {
                if (_budgetResetDate < today)
                {
                    _logger.LogInformation("Daily token budget reset. Yesterday used: {Tokens}", _tokensUsedToday);
                    _tokensUsedToday = 0;
                    _budgetResetDate = today;
                    _failureCounts.Clear();
                    _circuitOpenUntil.Clear();
                }
            }
        }
    }
}
