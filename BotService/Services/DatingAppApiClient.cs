using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BotService.Configuration;
using BotService.Models;
using Microsoft.Extensions.Options;

namespace BotService.Services;

/// <summary>
/// HTTP client that wraps all DatingApp API calls a bot needs to make.
/// Handles auth headers, serialization, and error logging.
/// Maps exactly to the endpoints used by the Flutter app.
/// </summary>
public class DatingAppApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DatingAppApiClient> _logger;
    private readonly ServiceEndpoints _endpoints;
    
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public DatingAppApiClient(
        HttpClient http,
        IOptions<BotServiceOptions> options,
        ILogger<DatingAppApiClient> logger)
    {
        _http = http;
        _logger = logger;
        _endpoints = options.Value.Endpoints;
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
            hasChildren = false
        };
        
        var response = await PostAsync($"{_endpoints.UserService}/api/UserProfiles", payload, token, ct);
        
        if (response == null) return null;
        
        // Parse profile ID from response
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
        
        // Result might be an array or an object with a data property
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

    // ─── Helpers ────────────────────────────────────────────────

    private async Task<JsonElement?> GetAsync(string url, string token, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GET {Url} returned {Status}", url, response.StatusCode);
                return null;
            }
            
            var json = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrEmpty(json)) return null;
            return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GET {Url} failed", url);
            return null;
        }
    }

    private async Task<JsonElement?> PostAsync(string url, object payload, string token, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("POST {Url} returned {Status}: {Body}", url, response.StatusCode, body);
                return null;
            }
            
            var json = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrEmpty(json)) return JsonSerializer.Deserialize<JsonElement>("{}");
            return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POST {Url} failed", url);
            return null;
        }
    }
}
