using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;

namespace BotService.Controllers;

/// <summary>
/// REST API for user-submitted feedback (voice memos + metadata) from the Flutter app.
/// Anonymous-friendly in non-Production environments. Audio files are stored on disk;
/// a laptop-side Whisper script fills in transcripts later via the PATCH endpoint.
/// </summary>
[ApiController]
[Route("api/userfeedback")]
[AllowAnonymous]
public class UserFeedbackController : ControllerBase
{
    private const long MaxAudioBytes = 8 * 1024 * 1024; // 8 MB cap
    private static readonly string[] AllowedExtensions = { ".m4a", ".aac", ".mp3", ".wav", ".ogg" };

    private readonly BotDbContext _db;
    private readonly ILogger<UserFeedbackController> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly IOptions<BotServiceOptions> _config;

    public UserFeedbackController(
        BotDbContext db,
        ILogger<UserFeedbackController> logger,
        IWebHostEnvironment env,
        IOptions<BotServiceOptions> config)
    {
        _db = db;
        _logger = logger;
        _env = env;
        _config = config;
    }

    /// <summary>Submit a feedback item. Multipart form-data: audio file (optional) + text fields.</summary>
    [HttpPost]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IActionResult> Submit([FromForm] UserFeedbackSubmission submission)
    {
        if (submission.Audio == null && string.IsNullOrWhiteSpace(submission.NoteText))
        {
            return BadRequest(new { error = "Provide either an audio file or a noteText." });
        }

        string? storedPath = null;
        if (submission.Audio != null)
        {
            if (submission.Audio.Length > MaxAudioBytes)
            {
                return BadRequest(new { error = $"Audio exceeds {MaxAudioBytes} bytes." });
            }
            var ext = Path.GetExtension(submission.Audio.FileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            {
                return BadRequest(new { error = $"Unsupported audio extension '{ext}'. Allowed: {string.Join(", ", AllowedExtensions)}" });
            }

            var dir = ResolveAudioDir();
            Directory.CreateDirectory(dir);
            var fileName = $"{Guid.NewGuid():N}{ext}";
            storedPath = Path.Combine(dir, fileName);
            await using var fs = System.IO.File.Create(storedPath);
            await submission.Audio.CopyToAsync(fs);
        }

        var entity = new UserFeedback
        {
            ReceivedAt = DateTime.UtcNow,
            AudioFilePath = storedPath,
            DurationSec = submission.DurationSec ?? 0,
            SubmitterKeycloakId = ExtractKeycloakSubject() ?? submission.SubmitterKeycloakId,
            Screen = Truncate(submission.Screen, 100),
            NoteText = Truncate(submission.NoteText, 2000),
            AppVersion = Truncate(submission.AppVersion, 40),
        };

        _db.UserFeedbacks.Add(entity);
        await _db.SaveChangesAsync();

        _logger.LogInformation("UserFeedback {Id} received (audio={HasAudio}, screen={Screen}, sub={Sub})",
            entity.Id, storedPath != null, entity.Screen, entity.SubmitterKeycloakId ?? "anon");

        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, ToDto(entity));
    }

    /// <summary>List feedback items (newest first). Optional unprocessed filter for transcription pipeline.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] bool? unprocessed,
        [FromQuery] DateTime? since,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (_env.IsProduction())
        {
            return Forbid();
        }

        var q = _db.UserFeedbacks.AsQueryable();
        if (unprocessed == true) q = q.Where(f => f.ProcessedAt == null);
        if (since.HasValue) q = q.Where(f => f.ReceivedAt >= since.Value);

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(f => f.ReceivedAt)
            .Skip((page - 1) * pageSize)
            .Take(Math.Clamp(pageSize, 1, 200))
            .Select(f => ToDto(f))
            .ToListAsync();

        return Ok(new { total, page, pageSize, items });
    }

    /// <summary>Get a single feedback row.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        if (_env.IsProduction()) return Forbid();
        var entity = await _db.UserFeedbacks.FindAsync(id);
        if (entity == null) return NotFound();
        return Ok(ToDto(entity));
    }

    /// <summary>Stream the audio file for a feedback row (used by the transcription script).</summary>
    [HttpGet("{id:int}/audio")]
    public async Task<IActionResult> GetAudio(int id)
    {
        if (_env.IsProduction()) return Forbid();
        var entity = await _db.UserFeedbacks.FindAsync(id);
        if (entity == null || string.IsNullOrEmpty(entity.AudioFilePath)) return NotFound();
        if (!System.IO.File.Exists(entity.AudioFilePath)) return NotFound();

        var ext = Path.GetExtension(entity.AudioFilePath).ToLowerInvariant();
        var mime = ext switch
        {
            ".m4a" or ".aac" => "audio/aac",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            _ => "application/octet-stream"
        };
        var stream = System.IO.File.OpenRead(entity.AudioFilePath);
        return File(stream, mime, Path.GetFileName(entity.AudioFilePath));
    }

    /// <summary>Update transcript (called by the laptop-side Whisper script).</summary>
    [HttpPatch("{id:int}")]
    public async Task<IActionResult> PatchTranscript(int id, [FromBody] TranscriptUpdate update)
    {
        if (_env.IsProduction()) return Forbid();
        var entity = await _db.UserFeedbacks.FindAsync(id);
        if (entity == null) return NotFound();

        entity.Transcript = Truncate(update.Transcript, 8000);
        entity.ProcessedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    /// <summary>Resolve the configured directory for feedback audio files.</summary>
    private string ResolveAudioDir()
    {
        var configured = _config.Value.Feedback.AudioPath;
        if (string.IsNullOrWhiteSpace(configured))
            configured = "Data/UserFeedback";
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(_env.ContentRootPath, configured);
    }

    private string? ExtractKeycloakSubject()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var raw = header.Substring("Bearer ".Length).Trim();
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(raw);
            return jwt.Subject;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UserFeedback: could not parse Authorization header");
            return null;
        }
    }

    private static string? Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));

    private static object ToDto(UserFeedback f) => new
    {
        id = f.Id,
        receivedAt = f.ReceivedAt,
        durationSec = f.DurationSec,
        screen = f.Screen,
        noteText = f.NoteText,
        appVersion = f.AppVersion,
        submitterKeycloakId = f.SubmitterKeycloakId,
        hasAudio = !string.IsNullOrEmpty(f.AudioFilePath),
        transcript = f.Transcript,
        processedAt = f.ProcessedAt,
    };
}

public class UserFeedbackSubmission
{
    public IFormFile? Audio { get; set; }
    public int? DurationSec { get; set; }
    public string? Screen { get; set; }
    public string? NoteText { get; set; }
    public string? AppVersion { get; set; }
    public string? SubmitterKeycloakId { get; set; }
}

public class TranscriptUpdate
{
    public string? Transcript { get; set; }
}
