using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BotService.Configuration;
using BotService.Models;
using BotService.Services.Observer;
using Microsoft.Extensions.Options;

namespace BotService.Services;

/// <summary>
/// HTTP client that wraps all DatingApp API calls a bot needs to make.
/// Handles auth headers, serialization, and error logging.
/// Instrumented with BotObserver for finding detection.
/// </summary>
public class DatingAppApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DatingAppApiClient> _logger;
    private readonly ServiceEndpoints _endpoints;
    private readonly BotObserver? _observer;
    
    // Current bot context for observer tagging (set per-call)
    private string _currentBotPersona = "unknown";
    private string _currentBotUserId = "unknown";
    
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public DatingAppApiClient(
        HttpClient http,
        IOptions<BotServiceOptions> options,
        ILogger<DatingAppApiClient> logger,
        BotObserver? observer = null)
    {
        _http = http;
        _logger = logger;
        _endpoints = options.Value.Endpoints;
        _observer = observer;
    }

    /// <summary>Set the current bot context for observer tagging</summary>
    public void SetBotContext(string personaId, string userId)
    {
        _currentBotPersona = personaId;
        _currentBotUserId = userId;
    }

    // ─── Profile ────────────────────────────────────────────────

    /// <summary>Create a user profile via POST /api/UserProfiles</summary>
    public async Task<int?> CreateProfileAsync(BotPersona persona, string token, CancellationToken ct)
    {
        var birthYear = DateTime.UtcNow.Year - persona.Age;
        var payload = new
        {
            name = $"{persona.FirstName} {persona.LastName}",
            email = $"bot_{persona.Id}@bot.local",
            bio = persona.Bio,
            gender = persona.Gender,
            preferences = persona.PreferredGender,
            dateOfBirth = new DateTime(birthYear, 6, 15).ToString("O"),
            city = persona.City,
            state = "Stockholm County",
            country = "Sweden",
            latitude = persona.Latitude,
            longitude = persona.Longitude,
            occupation = persona.Occupation,
            education = persona.Education,
            interests = persona.Interests,
            languages = persona.Languages,
            height = persona.Height,
            smokingStatus = persona.SmokingStatus,
            drinkingStatus = persona.DrinkingStatus,
            relationshipType = persona.RelationshipType,
            wantsChildren = false,
            hasChildren = false,
            isBot = true
        };
        
        var response = await PostAsync($"{_endpoints.UserService}/api/UserProfiles", payload, token, ct);
        
        if (response == null) return null;
        
        if (response.Value.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
            return idProp.GetInt32();
        if (response.Value.TryGetProperty("Id", out var idProp2) && idProp2.ValueKind == JsonValueKind.Number)
            return idProp2.GetInt32();
        if (response.Value.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.Object)
        {
            if (valueProp.TryGetProperty("id", out var nestedId))
                return nestedId.GetInt32();
        }
        
        _logger.LogWarning("Could not parse profile ID from response for bot {Id}", persona.Id);
        return null;
    }

    /// <summary>Get own profile via GET /api/profiles/me</summary>
    public async Task<JsonElement?> GetMyProfileAsync(string token, CancellationToken ct)
    {
        return await GetAsync($"{_endpoints.UserService}/api/profiles/me", token, ct);
    }

    // ─── Discover & Swipe ───────────────────────────────────────

    /// <summary>Get candidate profiles for swiping via GET /api/Matchmaking/profiles/{profileId}</summary>
    public async Task<JsonElement[]> GetCandidatesAsync(int profileId, string token, CancellationToken ct)
    {
        var result = await GetAsync($"{_endpoints.MatchmakingService}/api/Matchmaking/profiles/{profileId}", token, ct);
        if (result == null) return Array.Empty<JsonElement>();
        
        if (result.Value.ValueKind == JsonValueKind.Array)
            return result.Value.EnumerateArray().ToArray();
        if (result.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray().ToArray();
        
        return Array.Empty<JsonElement>();
    }

    /// <summary>Record a swipe via POST /api/Swipes</summary>
    public async Task<(bool Success, bool IsMutualMatch)> SwipeAsync(
        int fromProfileId, int targetProfileId, bool isLike, string token, CancellationToken ct)
    {
        var payload = new
        {
            userId = fromProfileId,
            targetUserId = targetProfileId,
            isLike,
            idempotencyKey = Guid.NewGuid().ToString()
        };
        
        var result = await PostAsync($"{_endpoints.SwipeService}/api/Swipes", payload, token, ct);
        if (result == null) return (false, false);
        
        var isMutual = result.Value.TryGetProperty("isMutualMatch", out var matchProp)
                       && matchProp.GetBoolean();
        
        return (true, isMutual);
    }

    // ─── Matches ────────────────────────────────────────────────

    /// <summary>Get bot's matches via GET /api/Swipes/matches/{profileId}</summary>
    public async Task<JsonElement[]> GetMatchesAsync(int profileId, string token, CancellationToken ct)
    {
        var result = await GetAsync($"{_endpoints.SwipeService}/api/Swipes/matches/{profileId}", token, ct);
        if (result == null) return Array.Empty<JsonElement>();
        
        if (result.Value.ValueKind == JsonValueKind.Array)
            return result.Value.EnumerateArray().ToArray();
        if (result.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray().ToArray();
        
        return Array.Empty<JsonElement>();
    }

    // ─── Messaging (REST) ───────────────────────────────────────

    /// <summary>Send a message via REST POST /api/Messages</summary>
    public async Task<bool> SendMessageAsync(
        string receiverKeycloakId, string content, string token, CancellationToken ct)
    {
        var payload = new
        {
            receiverId = receiverKeycloakId,
            content,
            type = "Text"
        };
        
        var result = await PostAsync($"{_endpoints.MessagingService}/api/Messages", payload, token, ct);
        return result != null;
    }

    /// <summary>Get conversations via GET /api/Messages/conversations</summary>
    public async Task<JsonElement[]> GetConversationsAsync(string token, CancellationToken ct)
    {
        var result = await GetAsync($"{_endpoints.MessagingService}/api/Messages/conversations", token, ct);
        if (result == null) return Array.Empty<JsonElement>();
        
        if (result.Value.ValueKind == JsonValueKind.Array)
            return result.Value.EnumerateArray().ToArray();
        
        return Array.Empty<JsonElement>();
    }

    /// <summary>
    /// Get message history with a specific user via GET /api/Messages/conversation/{otherUserId}.
    /// Returns ChatMessage list for LLM context building.
    /// </summary>
    public async Task<List<Llm.ChatMessage>> GetConversationMessagesAsync(
        string otherUserId, string token, CancellationToken ct)
    {
        var result = await GetAsync(
            $"{_endpoints.MessagingService}/api/Messages/conversation/{otherUserId}", token, ct);
        
        if (result == null) return new List<Llm.ChatMessage>();

        var messages = new List<Llm.ChatMessage>();
        var items = result.Value.ValueKind == JsonValueKind.Array
            ? result.Value.EnumerateArray()
            : (result.Value.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray()
                : Enumerable.Empty<JsonElement>());

        foreach (var msg in items)
        {
            var senderId = msg.TryGetProperty("senderId", out var s) ? s.GetString() ?? "" : "";
            var recipientId = msg.TryGetProperty("receiverId", out var r) ? r.GetString() ?? ""
                : msg.TryGetProperty("recipientId", out var r2) ? r2.GetString() ?? "" : "";
            var content = msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var sentAt = msg.TryGetProperty("sentAt", out var t) && t.TryGetDateTime(out var dt)
                ? dt : DateTime.UtcNow;

            messages.Add(new Llm.ChatMessage
            {
                SenderUserId = senderId,
                RecipientUserId = recipientId,
                Content = content,
                SentAt = sentAt
            });
        }

        return messages;
    }

    // ─── Photo Upload ──────────────────────────────────────────

    /// <summary>Upload a profile photo for the authenticated bot user</summary>
    public async Task<bool> UploadPhotoAsync(byte[] imageBytes, string fileName, string token, CancellationToken ct)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(imageBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Add(fileContent, "Photo", fileName);
            content.Add(new StringContent("true"), "IsPrimary");
            content.Add(new StringContent("1"), "DisplayOrder");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoints.PhotoService}/api/Photos")
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Photo uploaded successfully for bot");
                return true;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Photo upload failed: {Status} {Body}", response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Photo upload exception");
            return false;
        }
    }

    // ─── Safety (block/report awareness) ────────────────────────

    /// <summary>Check if a specific user has blocked us</summary>
    public async Task<bool> IsBlockedByUserAsync(
        string targetKeycloakId, string token, CancellationToken ct)
    {
        try
        {
            var result = await GetAsync(
                $"{_endpoints.SafetyService}/api/safety/block/{targetKeycloakId}", token, ct);
            if (result == null) return false;
            if (result.Value.ValueKind == JsonValueKind.Object &&
                result.Value.TryGetProperty("isBlocked", out var blocked))
                return blocked.GetBoolean();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Get set of user IDs who have blocked this bot</summary>
    public async Task<HashSet<string>> GetBlockedByIdsAsync(string token, CancellationToken ct)
    {
        try
        {
            var result = await GetAsync(
                $"{_endpoints.SafetyService}/api/safety/block", token, ct);
            if (result == null) return new HashSet<string>();

            var ids = new HashSet<string>();
            if (result.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in result.Value.EnumerateArray())
                {
                    if (item.TryGetProperty("blockedUserId", out var idProp))
                        ids.Add(idProp.GetString() ?? "");
                }
            }
            return ids;
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    // ─── Instrumented Helpers ───────────────────────────────────

    private string DetectService(string url)
    {
        if (url.Contains("/api/UserProfiles") || url.Contains("/api/profiles"))
            return "UserService";
        if (url.Contains("/api/Matchmaking"))
            return "MatchmakingService";
        if (url.Contains("/api/Swipes"))
            return "SwipeService";
        if (url.Contains("/api/Messages"))
            return "MessagingService";
        if (url.Contains("/api/safety"))
            return "SafetyService";
        return "Unknown";
    }

    private async Task<JsonElement?> GetAsync(string url, string token, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var response = await _http.SendAsync(request, ct);
            sw.Stop();
            
            var statusCode = (int)response.StatusCode;
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("GET {Url} returned {Status}", url, response.StatusCode);
                
                if (_observer != null)
                    await _observer.ObserveApiCall(DetectService(url), url, statusCode, sw.ElapsedMilliseconds,
                        _currentBotPersona, _currentBotUserId, errorBody);
                return null;
            }
            
            // Observe successful calls too (for latency tracking)
            if (_observer != null)
                await _observer.ObserveApiCall(DetectService(url), url, statusCode, sw.ElapsedMilliseconds,
                    _currentBotPersona, _currentBotUserId);
            
            var json = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrEmpty(json)) return null;
            return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            if (_observer != null)
                await _observer.ObserveTimeout(DetectService(url), url, _currentBotPersona, _currentBotUserId);
            _logger.LogError("GET {Url} timed out", url);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate shutdown cancellation
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "GET {Url} failed", url);
            return null;
        }
    }

    private async Task<JsonElement?> PostAsync(string url, object payload, string token, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var response = await _http.SendAsync(request, ct);
            sw.Stop();
            
            var statusCode = (int)response.StatusCode;
            
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("POST {Url} returned {Status}: {Body}", url, response.StatusCode, body);
                
                if (_observer != null)
                    await _observer.ObserveApiCall(DetectService(url), url, statusCode, sw.ElapsedMilliseconds,
                        _currentBotPersona, _currentBotUserId, body);
                return null;
            }
            
            if (_observer != null)
                await _observer.ObserveApiCall(DetectService(url), url, statusCode, sw.ElapsedMilliseconds,
                    _currentBotPersona, _currentBotUserId);
            
            var json = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrEmpty(json)) return JsonSerializer.Deserialize<JsonElement>("{}");
            return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            if (_observer != null)
                await _observer.ObserveTimeout(DetectService(url), url, _currentBotPersona, _currentBotUserId);
            _logger.LogError("POST {Url} timed out", url);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "POST {Url} failed", url);
            return null;
        }
    }
}
