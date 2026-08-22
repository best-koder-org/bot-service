using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BotService.Services.Whisper;

/// <summary>
/// Server-side transcription pump for voice feedback — replaces the laptop-side
/// <c>scripts/process-feedback.py</c> watcher.
/// Polls the local DB for unprocessed feedback, reads the audio straight from
/// disk, calls the whisper-service engine, and writes the transcript back.
/// Runs inside bot-service so no downloads, gateway hops, or rate limits are
/// involved. Rows stay unprocessed (and are retried) while the engine is down;
/// genuinely undecodable audio is marked "[unreadable audio]" so it drops out.
/// </summary>
public class WhisperTranscriptionService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WhisperTranscriptionService> _logger;
    private readonly IOptionsMonitor<BotServiceOptions> _config;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WhisperTranscriptionService(
        IServiceProvider serviceProvider,
        ILogger<WhisperTranscriptionService> logger,
        IOptionsMonitor<BotServiceOptions> config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WhisperTranscriptionService starting");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _config.CurrentValue;
            if (!opts.Enabled || !opts.Whisper.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                continue;
            }

            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Whisper transcription pass error");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, opts.Whisper.IntervalSeconds)), stoppingToken);
        }

        _logger.LogInformation("WhisperTranscriptionService stopped");
    }

    /// <summary>Process up to BatchSize unprocessed feedback rows. Exposed for tests.</summary>
    internal async Task RunPassAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var whisper = scope.ServiceProvider.GetRequiredService<IWhisperApiClient>();
            var opts = _config.CurrentValue.Whisper;

            var pending = await db.UserFeedbacks
                .Where(f => f.ProcessedAt == null)
                .OrderBy(f => f.ReceivedAt)
                .Take(Math.Max(1, opts.BatchSize))
                .ToListAsync(ct);

            if (pending.Count == 0) return;

            _logger.LogInformation("🗣️ Whisper pass: {Count} unprocessed feedback item(s)", pending.Count);

            foreach (var item in pending)
            {
                if (ct.IsCancellationRequested) break;
                await ProcessItemAsync(db, whisper, item, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessItemAsync(
        BotDbContext db, IWhisperApiClient whisper, UserFeedback item, CancellationToken ct)
    {
        // Text-only row: nothing to transcribe — mark processed with the note as transcript.
        if (string.IsNullOrEmpty(item.AudioFilePath))
        {
            item.Transcript = Truncate(item.NoteText, 8000);
            item.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("📝 Feedback {Id}: text-only marked processed", item.Id);
            return;
        }

        if (!System.IO.File.Exists(item.AudioFilePath))
        {
            item.Transcript = "[audio file missing]";
            item.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Feedback {Id}: audio file missing ({Path})", item.Id, item.AudioFilePath);
            return;
        }

        try
        {
            await using var fs = System.IO.File.OpenRead(item.AudioFilePath);
            var transcript = await whisper.TranscribeAsync(fs, Path.GetFileName(item.AudioFilePath), ct);

            if (string.IsNullOrWhiteSpace(transcript))
            {
                // 200 but no text → corrupt / undecodable audio. Mark it so the row
                // drops out of the unprocessed queue (parity with the old script).
                item.Transcript = "[unreadable audio]";
                item.ProcessedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                _logger.LogWarning("Feedback {Id}: no transcript (corrupt audio), marked [unreadable audio]", item.Id);
                return;
            }

            item.Transcript = Truncate(transcript, 8000);
            item.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("✅ Feedback {Id} transcribed ({Chars} chars)", item.Id, item.Transcript.Length);
        }
        catch (WhisperEngineUnavailableException ex)
        {
            // Engine down / transient — leave unprocessed, retry on the next pass.
            _logger.LogWarning("Feedback {Id}: whisper engine unavailable ({Reason}), will retry", item.Id, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — drop out.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Feedback {Id}: transcription error", item.Id);
        }
    }

    private static string? Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
