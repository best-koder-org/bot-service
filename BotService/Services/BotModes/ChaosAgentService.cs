using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using BotService.Services.Keycloak;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BotService.Services.BotModes;

/// <summary>
/// Intentionally does weird things to find bugs:
/// - Rapid swipe/unswipe cycles
/// - Invalid payloads (empty fields, wrong types)
/// - Rate limit violations (sends bursts)
/// - Messages to non-matched users
/// - Duplicate submissions
/// Reports which scenarios triggered errors vs were handled gracefully.
/// </summary>
public class ChaosAgentService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChaosAgentService> _logger;
    private readonly IOptionsMonitor<BotServiceOptions> _config;
    private readonly BotPersonaEngine _personaEngine;
    private readonly Random _random = new();

    public ChaosAgentService(
        IServiceProvider serviceProvider,
        ILogger<ChaosAgentService> logger,
        IOptionsMonitor<BotServiceOptions> config,
        BotPersonaEngine personaEngine)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
        _personaEngine = personaEngine;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ChaosAgentService starting");
        await Task.Delay(TimeSpan.FromSeconds(_config.CurrentValue.StartupDelaySec + 30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _config.CurrentValue;
            if (!opts.Enabled || !opts.Modes.Chaos.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                continue;
            }

            try
            {
                await RunChaosScenarioAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chaos scenario error");
            }

            await Task.Delay(TimeSpan.FromSeconds(opts.Modes.Chaos.CycleIntervalSec), stoppingToken);
        }

        _logger.LogInformation("ChaosAgentService stopped");
    }

    private async Task RunChaosScenarioAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
        var endpoints = _config.CurrentValue.Endpoints;

        var chaosBot = await db.BotStates
            .Where(b => b.Status == BotStatus.Active && b.AccessToken != null)
            .FirstOrDefaultAsync(ct);

        if (chaosBot == null)
        {
            _logger.LogDebug("No active bots available for chaos testing");
            return;
        }

        var enabledScenarios = _config.CurrentValue.Modes.Chaos.EnabledScenarios;
        var scenario = enabledScenarios[_random.Next(enabledScenarios.Count)];

        _logger.LogInformation("🔥 CHAOS: Running scenario '{Scenario}'", scenario);

        switch (scenario)
        {
            case "invalid-payload":
                await TestInvalidPayloadsAsync(chaosBot, endpoints, http, ct);
                break;
            case "rapid-swipe":
                await TestRapidSwipeAsync(chaosBot, endpoints, http, ct);
                break;
            case "exceed-rate-limit":
                await TestRateLimitAsync(chaosBot, endpoints, http, ct);
                break;
            case "duplicate-submission":
                await TestDuplicateSubmissionAsync(chaosBot, endpoints, http, ct);
                break;
            case "empty-auth":
                await TestEmptyAuthAsync(endpoints, http, ct);
                break;
            default:
                _logger.LogWarning("Unknown chaos scenario: {Scenario}", scenario);
                break;
        }
    }

    private async Task TestInvalidPayloadsAsync(BotState bot, ServiceEndpoints ep, HttpClient http, CancellationToken ct)
    {
        var cases = new[]
        {
            ("POST", $"{ep.SwipeService}/api/Swipes", "{}"),
            ("POST", $"{ep.SwipeService}/api/Swipes", "{\"userId\": -1, \"targetUserId\": -1, \"isLike\": \"maybe\"}"),
            ("POST", $"{ep.MessagingService}/api/Messages", "{\"receiverId\": \"\", \"content\": \"\"}"),
            ("POST", $"{ep.UserService}/api/UserProfiles", "{\"name\": \"\"}"),
        };

        foreach (var (method, url, body) in cases)
        {
            try
            {
                var request = new HttpRequestMessage(new HttpMethod(method), url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bot.AccessToken);
                
                var response = await http.SendAsync(request, ct);
                var status = (int)response.StatusCode;
                var isGraceful = status >= 400 && status < 500;
                
                _logger.LogInformation(
                    "🔥 CHAOS invalid-payload: {Method} {Url} → {Status} ({Result})",
                    method, url, status, isGraceful ? "✅ Gracefully rejected" : "⚠️ Unexpected");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("🔥 CHAOS invalid-payload: {Method} {Url} → EXCEPTION: {Msg}",
                    method, url, ex.Message);
            }
        }
    }

    private async Task TestRapidSwipeAsync(BotState bot, ServiceEndpoints ep, HttpClient http, CancellationToken ct)
    {
        _logger.LogInformation("🔥 CHAOS: Rapid-fire 20 swipes in 2 seconds");
        
        for (int i = 0; i < 20; i++)
        {
            var payload = JsonSerializer.Serialize(new
            {
                userId = bot.ProfileId,
                targetUserId = 99999 + i,
                isLike = true,
                idempotencyKey = Guid.NewGuid().ToString()
            });
            
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ep.SwipeService}/api/Swipes")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bot.AccessToken);
            
            try
            {
                var response = await http.SendAsync(request, ct);
                if ((int)response.StatusCode == 429)
                {
                    _logger.LogInformation("🔥 CHAOS rapid-swipe: Rate limited after {Count} requests ✅", i + 1);
                    return;
                }
            }
            catch { /* Expected under load */ }
            
            await Task.Delay(100, ct); // 100ms between requests
        }
        
        _logger.LogWarning("🔥 CHAOS rapid-swipe: Completed 20 requests without rate limiting ⚠️");
    }

    private async Task TestRateLimitAsync(BotState bot, ServiceEndpoints ep, HttpClient http, CancellationToken ct)
    {
        _logger.LogInformation("🔥 CHAOS: Testing rate limit on messaging (10 req/min limit)");
        
        for (int i = 0; i < 15; i++)
        {
            var payload = JsonSerializer.Serialize(new
            {
                receiverId = "00000000-0000-0000-0000-000000000000",
                content = $"Chaos test message #{i}",
                type = "Text"
            });
            
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ep.MessagingService}/api/Messages")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bot.AccessToken);
            
            try
            {
                var response = await http.SendAsync(request, ct);
                if ((int)response.StatusCode == 429)
                {
                    _logger.LogInformation("🔥 CHAOS rate-limit: Messaging rate limited after {Count} requests ✅", i + 1);
                    return;
                }
            }
            catch { }
        }
        
        _logger.LogWarning("🔥 CHAOS rate-limit: No rate limiting detected on messaging ⚠️");
    }

    private async Task TestDuplicateSubmissionAsync(BotState bot, ServiceEndpoints ep, HttpClient http, CancellationToken ct)
    {
        var idempotencyKey = Guid.NewGuid().ToString();
        var payload = JsonSerializer.Serialize(new
        {
            userId = bot.ProfileId,
            targetUserId = 99999,
            isLike = true,
            idempotencyKey
        });

        _logger.LogInformation("🔥 CHAOS: Sending same swipe 3 times with same idempotency key");
        
        for (int i = 0; i < 3; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ep.SwipeService}/api/Swipes")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bot.AccessToken);
            
            try
            {
                var response = await http.SendAsync(request, ct);
                _logger.LogInformation("🔥 CHAOS duplicate: Attempt {N} → {Status}",
                    i + 1, (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("🔥 CHAOS duplicate: Attempt {N} → EXCEPTION: {Msg}", i + 1, ex.Message);
            }
        }
    }

    private async Task TestEmptyAuthAsync(ServiceEndpoints ep, HttpClient http, CancellationToken ct)
    {
        var endpoints = new[]
        {
            $"{ep.UserService}/api/profiles/me",
            $"{ep.SwipeService}/api/Swipes",
            $"{ep.MessagingService}/api/Messages/conversations",
            $"{ep.MatchmakingService}/api/Matchmaking/matches"
        };

        _logger.LogInformation("🔥 CHAOS: Testing endpoints without auth token");
        
        foreach (var url in endpoints)
        {
            try
            {
                var response = await http.GetAsync(url, ct);
                var status = (int)response.StatusCode;
                var isSecure = status == 401 || status == 403;
                
                _logger.LogInformation("🔥 CHAOS no-auth: GET {Url} → {Status} ({Result})",
                    url, status, isSecure ? "✅ Properly secured" : "⚠️ SECURITY ISSUE");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("🔥 CHAOS no-auth: {Url} → EXCEPTION: {Msg}", url, ex.Message);
            }
        }
    }
}
