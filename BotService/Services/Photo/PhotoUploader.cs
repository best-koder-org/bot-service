using System.Net.Http.Headers;
using BotService.Configuration;
using Microsoft.Extensions.Options;

namespace BotService.Services.Photo;

/// <summary>
/// T361 — Uploads generated bot photos to the photo-service.
/// Uses multipart/form-data POST to /api/photos with bot auth.
/// </summary>
public class PhotoUploader
{
    private readonly HttpClient _http;
    private readonly IOptions<BotServiceOptions> _options;
    private readonly ILogger<PhotoUploader> _logger;
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public PhotoUploader(
        HttpClient http,
        IOptions<BotServiceOptions> options,
        ILogger<PhotoUploader> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Upload a photo for a bot persona to the photo-service.
    /// </summary>
    /// <param name="personaId">Bot persona ID (used as Keycloak user ID)</param>
    /// <param name="imageData">JPEG/PNG image bytes</param>
    /// <param name="isPrimary">Set as primary profile photo</param>
    /// <param name="displayOrder">Display order (1-based)</param>
    /// <returns>Photo ID from photo-service, or null on failure</returns>
    public async Task<int?> UploadPhotoAsync(
        string personaId,
        byte[] imageData,
        bool isPrimary = false,
        int? displayOrder = null,
        CancellationToken ct = default)
    {
        try
        {
            var photoServiceUrl = _options.Value.Endpoints.PhotoService;
            var url = $"{photoServiceUrl}/api/photos";

            using var content = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(imageData);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(imageContent, "Photo", $"{personaId}_portrait.jpg");

            if (displayOrder.HasValue)
                content.Add(new StringContent(displayOrder.Value.ToString()), "DisplayOrder");
            if (isPrimary)
                content.Add(new StringContent("true"), "IsPrimary");

            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            // Add auth header (use bot-service internal API key for simplicity)
            var apiKey = _options.Value.Endpoints.InternalApiKey;
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Add("X-Internal-API-Key", apiKey);

            var response = await _http.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                // Parse photo ID from response
                var json = System.Text.Json.JsonDocument.Parse(responseBody);
                if (json.RootElement.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
                {
                    _logger.LogInformation("Uploaded photo for {PersonaId}: photoId={PhotoId}", personaId, id);
                    return id;
                }
                _logger.LogInformation("Uploaded photo for {PersonaId} (status={Status})", personaId, response.StatusCode);
                return 0; // Upload succeeded but couldn't parse ID
            }

            _logger.LogWarning("Photo upload failed for {PersonaId}: {Status} — {Body}",
                personaId, response.StatusCode, responseBody[..Math.Min(200, responseBody.Length)]);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Photo upload error for {PersonaId}", personaId);
            return null;
        }
    }

    /// <summary>
    /// Batch-upload a portrait + lifestyle photos for a persona.
    /// Returns list of photo IDs.
    /// </summary>
    public async Task<List<int>> UploadPersonaPhotosAsync(
        string personaId,
        byte[] portrait,
        List<(string description, byte[] data)> lifestylePhotos,
        CancellationToken ct = default)
    {
        var photoIds = new List<int>();

        // Upload portrait as primary
        var portraitId = await UploadPhotoAsync(personaId, portrait, isPrimary: true, displayOrder: 1, ct: ct);
        if (portraitId.HasValue) photoIds.Add(portraitId.Value);

        // Upload lifestyle photos
        var order = 2;
        foreach (var (_, data) in lifestylePhotos)
        {
            var id = await UploadPhotoAsync(personaId, data, isPrimary: false, displayOrder: order++, ct: ct);
            if (id.HasValue) photoIds.Add(id.Value);
        }

        return photoIds;
    }
}
