namespace BotService.Models;

/// <summary>
/// User-submitted feedback (voice memo + metadata) captured from the Flutter app via the in-app feedback FAB.
/// Audio is stored on disk; transcript is filled in later by a laptop-side Whisper script.
/// </summary>
public class UserFeedback
{
    public int Id { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Absolute or relative path to the persisted audio file (.m4a/.aac). Null if text-only submission.</summary>
    public string? AudioFilePath { get; set; }

    /// <summary>Duration of the audio recording in seconds (client-reported). 0 for text-only.</summary>
    public int DurationSec { get; set; }

    /// <summary>Keycloak subject of the logged-in user when submitted. Null = anonymous.</summary>
    public string? SubmitterKeycloakId { get; set; }

    /// <summary>App route/screen name when submitted (e.g. "matches", "chat"). Optional.</summary>
    public string? Screen { get; set; }

    /// <summary>Optional text note typed by the user as fallback or supplement to the voice memo.</summary>
    public string? NoteText { get; set; }

    /// <summary>App version (semver). Optional.</summary>
    public string? AppVersion { get; set; }

    /// <summary>Filled by the laptop-side transcription script after Whisper runs.</summary>
    public string? Transcript { get; set; }

    /// <summary>Set when the transcription pass completes (success or failure).</summary>
    public DateTime? ProcessedAt { get; set; }
}
