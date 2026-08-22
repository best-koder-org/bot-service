namespace BotService.Services.Whisper;

/// <summary>
/// Thrown when the whisper-service engine cannot transcribe right now
/// (unreachable, timeout, or non-2xx response). Feedback rows are left
/// unprocessed and retried on the next pass.
/// </summary>
public sealed class WhisperEngineUnavailableException : Exception
{
    public WhisperEngineUnavailableException(string message) : base(message) { }
    public WhisperEngineUnavailableException(string message, Exception inner) : base(message, inner) { }
}
