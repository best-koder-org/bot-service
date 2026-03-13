using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BotService.Services.Keycloak;

/// <summary>
/// Manages Keycloak user lifecycle for bots: create users, get tokens, refresh tokens.
/// Follows the exact same auth flow as api_tests.py and the Flutter app.
/// </summary>
public class KeycloakBotProvisioner
{
    private readonly HttpClient _http;
    private readonly ILogger<KeycloakBotProvisioner> _logger;
    private readonly KeycloakOptions _config;
    private string? _adminToken;
    private DateTime _adminTokenExpiry = DateTime.MinValue;
    
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public KeycloakBotProvisioner(
        HttpClient http,
        IOptions<BotServiceOptions> options,
        ILogger<KeycloakBotProvisioner> logger)
    {
        _http = http;
        _logger = logger;
        _config = options.Value.Keycloak;
    }

    /// <summary>
    /// Ensure a Keycloak user exists for the bot persona. Returns the Keycloak user ID.
    /// Creates the user if it doesn't exist, reuses if it does.
    /// </summary>
    public async Task<string> EnsureBotUserAsync(BotPersona persona, CancellationToken ct = default)
    {
        var adminToken = await GetAdminTokenAsync(ct);
        var username = $"bot_{persona.Id}";
        var email = $"bot_{persona.Id}@bot.local";
        var password = $"{_config.BotPasswordPrefix}{persona.Id}";
        
        // Check if user already exists
        var existingId = await FindUserIdAsync(username, adminToken, ct);
        if (existingId != null)
        {
            _logger.LogDebug("Reusing existing Keycloak user {Username} ({Id})", username, existingId);
            return existingId;
        }
        
        // Create new user
        var createUrl = $"{_config.BaseUrl}/admin/realms/{_config.Realm}/users";
        var payload = new
        {
            username,
            email,
            firstName = persona.FirstName,
            lastName = persona.LastName,
            enabled = true,
            emailVerified = true,
            attributes = new Dictionary<string, string[]>
            {
                ["is_bot"] = new[] { "true" },
                ["persona_id"] = new[] { persona.Id }
            }
        };
        
        var request = new HttpRequestMessage(HttpMethod.Post, createUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        
        var response = await _http.SendAsync(request, ct);
        
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Race condition — someone else created it
            var id = await FindUserIdAsync(username, adminToken, ct);
            if (id != null) return id;
            throw new InvalidOperationException($"Keycloak user conflict but cannot find {username}");
        }
        
        response.EnsureSuccessStatusCode();
        
        // Extract user ID from Location header
        var location = response.Headers.Location?.ToString() ?? "";
        var userId = location.Split('/').LastOrDefault() ?? "";
        
        if (string.IsNullOrEmpty(userId))
        {
            userId = await FindUserIdAsync(username, adminToken, ct) 
                     ?? throw new InvalidOperationException($"Cannot determine Keycloak ID for {username}");
        }
        
        // Set password
        await SetPasswordAsync(userId, password, adminToken, ct);
        
        _logger.LogInformation("Created Keycloak bot user {Username} ({Id})", username, userId);
        return userId;
    }

    /// <summary>Get a user access token for the bot</summary>
    public async Task<(string AccessToken, string RefreshToken, DateTime ExpiresAt)> GetBotTokenAsync(
        BotPersona persona, CancellationToken ct = default)
    {
        var username = $"bot_{persona.Id}";
        var password = $"{_config.BotPasswordPrefix}{persona.Id}";
        
        var tokenUrl = $"{_config.BaseUrl}/realms/{_config.Realm}/protocol/openid-connect/token";
        var payload = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _config.ClientId,
            ["username"] = username,
            ["password"] = password,
            ["scope"] = "openid profile email offline_access"
        });
        
        var response = await _http.PostAsync(tokenUrl, payload, ct);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(ct);
        var tokenData = JsonSerializer.Deserialize<JsonElement>(json);
        
        var accessToken = tokenData.GetProperty("access_token").GetString()!;
        var refreshToken = tokenData.GetProperty("refresh_token").GetString()!;
        var expiresIn = tokenData.GetProperty("expires_in").GetInt32();
        var expiresAt = DateTime.UtcNow.AddSeconds(expiresIn - 30); // 30s buffer
        
        return (accessToken, refreshToken, expiresAt);
    }

    /// <summary>Refresh an existing token</summary>
    public async Task<(string AccessToken, string RefreshToken, DateTime ExpiresAt)> RefreshBotTokenAsync(
        string refreshToken, CancellationToken ct = default)
    {
        var tokenUrl = $"{_config.BaseUrl}/realms/{_config.Realm}/protocol/openid-connect/token";
        var payload = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _config.ClientId,
            ["refresh_token"] = refreshToken
        });
        
        var response = await _http.PostAsync(tokenUrl, payload, ct);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(ct);
        var tokenData = JsonSerializer.Deserialize<JsonElement>(json);
        
        var newAccessToken = tokenData.GetProperty("access_token").GetString()!;
        var newRefreshToken = tokenData.GetProperty("refresh_token").GetString()!;
        var expiresIn = tokenData.GetProperty("expires_in").GetInt32();
        var expiresAt = DateTime.UtcNow.AddSeconds(expiresIn - 30);
        
        return (newAccessToken, newRefreshToken, expiresAt);
    }

    private async Task<string> GetAdminTokenAsync(CancellationToken ct)
    {
        if (_adminToken != null && DateTime.UtcNow < _adminTokenExpiry)
            return _adminToken;
        
        var tokenUrl = $"{_config.BaseUrl}/realms/master/protocol/openid-connect/token";
        var payload = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = _config.AdminUser,
            ["password"] = _config.AdminPassword
        });
        
        var response = await _http.PostAsync(tokenUrl, payload, ct);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(ct);
        var tokenData = JsonSerializer.Deserialize<JsonElement>(json);
        
        _adminToken = tokenData.GetProperty("access_token").GetString()!;
        var expiresIn = tokenData.GetProperty("expires_in").GetInt32();
        _adminTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 30);
        
        return _adminToken;
    }

    private async Task<string?> FindUserIdAsync(string username, string adminToken, CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/admin/realms/{_config.Realm}/users?username={username}&exact=true";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        
        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        
        var json = await response.Content.ReadAsStringAsync(ct);
        var users = JsonSerializer.Deserialize<JsonElement[]>(json, JsonOpts);
        
        return users?.Length > 0 ? users[0].GetProperty("id").GetString() : null;
    }

    private async Task SetPasswordAsync(string userId, string password, string adminToken, CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/admin/realms/{_config.Realm}/users/{userId}/reset-password";
        var payload = new { type = "password", value = password, temporary = false };
        
        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        
        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
