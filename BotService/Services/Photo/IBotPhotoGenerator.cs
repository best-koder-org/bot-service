using BotService.Models;

namespace BotService.Services.Photo;

/// <summary>
/// Generates profile photos for bot personas.
/// Implementations: Placeholder (colored initials), Stability AI, Stitch MCP.
/// </summary>
public interface IBotPhotoGenerator
{
    /// <summary>
    /// Generate a profile photo for a bot persona.
    /// Returns image bytes (JPEG/PNG).
    /// </summary>
    Task<byte[]> GeneratePortraitAsync(BotPersona persona, CancellationToken ct = default);

    /// <summary>
    /// Generate 2-3 lifestyle photos for a bot persona.
    /// Returns list of (description, image bytes).
    /// </summary>
    Task<List<(string description, byte[] data)>> GenerateLifestylePhotosAsync(
        BotPersona persona, CancellationToken ct = default);

    /// <summary>
    /// Whether this generator can produce real photos (vs placeholders).
    /// </summary>
    bool IsRealPhotoGenerator { get; }
}
