namespace BotService.Services.Whisper;

/// <summary>Abstraction over the whisper-service transcription engine.</summary>
public interface IWhisperApiClient
{
    /// <summary>
    /// Transcribe an audio stream via the whisper-service engine.
    /// </summary>
    /// <param name="audio">Audio bytes (.m4a/.mp3/.wav/.ogg).</param>
    /// <param name="fileName">Original file name (used for the multipart part).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Trimmed transcript text, or <c>null</c> when the audio produced
    /// no text (e.g. corrupt / undecodable file, or silence).</returns>
    /// <exception cref="WhisperEngineUnavailableException">Engine unreachable,
    /// timed out, or returned an error status — caller should retry later.</exception>
    Task<string?> TranscribeAsync(Stream audio, string fileName, CancellationToken ct);
}
