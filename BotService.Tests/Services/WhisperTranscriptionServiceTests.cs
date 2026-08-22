using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using BotService.Services.Whisper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotService.Tests.Services;

public class WhisperTranscriptionServiceTests : IDisposable
{
    private readonly BotDbContext _db;
    private readonly Mock<IWhisperApiClient> _whisper = new();

    public WhisperTranscriptionServiceTests()
    {
        var options = new DbContextOptionsBuilder<BotDbContext>()
            .UseInMemoryDatabase(databaseName: $"WhisperSvcDb-{Guid.NewGuid()}")
            .Options;
        _db = new BotDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private WhisperTranscriptionService CreateService(Action<WhisperOptions>? configure = null)
    {
        var opts = new BotServiceOptions
        {
            Enabled = true,
            Whisper = new WhisperOptions
            {
                Enabled = true,
                IntervalSeconds = 30,
                BatchSize = 5,
                BaseUrl = "http://whisper:8095"
            }
        };
        configure?.Invoke(opts.Whisper);

        var provider = new ServiceCollection()
            .AddSingleton(_db)
            .AddSingleton<IWhisperApiClient>(_whisper.Object)
            .BuildServiceProvider();

        var monitor = new Mock<IOptionsMonitor<BotServiceOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(opts);

        return new WhisperTranscriptionService(
            provider,
            NullLogger<WhisperTranscriptionService>.Instance,
            monitor.Object);
    }

    private static string CreateTempAudioFile(string name = "clip.m4a")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"whisper-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        return path;
    }

    private static string CreateTempDir()
    {
        return Path.Combine(Path.GetTempPath(), $"whisper-test-{Guid.NewGuid():N}");
    }

    [Fact]
    public async Task RunPass_TextOnlyRow_IsMarkedProcessed_WithNoteAsTranscript()
    {
        _db.UserFeedbacks.Add(new UserFeedback
        {
            ReceivedAt = DateTime.UtcNow,
            NoteText = "Allt funkar bra!",
            AudioFilePath = null
        });
        await _db.SaveChangesAsync();

        var service = CreateService();
        await service.RunPassAsync(CancellationToken.None);

        var row = await _db.UserFeedbacks.SingleAsync();
        Assert.Equal("Allt funkar bra!", row.Transcript);
        Assert.NotNull(row.ProcessedAt);
        _whisper.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunPass_AudioRow_IsTranscribed_WhenEngineReturnsText()
    {
        var audioPath = CreateTempAudioFile();
        try
        {
            _db.UserFeedbacks.Add(new UserFeedback
            {
                ReceivedAt = DateTime.UtcNow,
                AudioFilePath = audioPath
            });
            await _db.SaveChangesAsync();

            _whisper
                .Setup(w => w.TranscribeAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("Det var kul att träffas!");

            var service = CreateService();
            await service.RunPassAsync(CancellationToken.None);

            var row = await _db.UserFeedbacks.SingleAsync();
            Assert.Equal("Det var kul att träffas!", row.Transcript);
            Assert.NotNull(row.ProcessedAt);
            _whisper.Verify(
                w => w.TranscribeAsync(It.IsAny<Stream>(), "clip.m4a", It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(audioPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task RunPass_AudioRow_IsMarkedUnreadable_WhenEngineReturnsNoText()
    {
        var audioPath = CreateTempAudioFile();
        try
        {
            _db.UserFeedbacks.Add(new UserFeedback
            {
                ReceivedAt = DateTime.UtcNow,
                AudioFilePath = audioPath
            });
            await _db.SaveChangesAsync();

            _whisper
                .Setup(w => w.TranscribeAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            var service = CreateService();
            await service.RunPassAsync(CancellationToken.None);

            var row = await _db.UserFeedbacks.SingleAsync();
            Assert.Equal("[unreadable audio]", row.Transcript);
            Assert.NotNull(row.ProcessedAt);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(audioPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task RunPass_AudioRow_StaysUnprocessed_WhenEngineUnavailable()
    {
        var audioPath = CreateTempAudioFile();
        try
        {
            _db.UserFeedbacks.Add(new UserFeedback
            {
                ReceivedAt = DateTime.UtcNow,
                AudioFilePath = audioPath
            });
            await _db.SaveChangesAsync();

            _whisper
                .Setup(w => w.TranscribeAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new WhisperEngineUnavailableException("unreachable"));

            var service = CreateService();
            await service.RunPassAsync(CancellationToken.None);

            var row = await _db.UserFeedbacks.SingleAsync();
            Assert.Null(row.Transcript);
            Assert.Null(row.ProcessedAt); // retried next pass
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(audioPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task RunPass_MissingAudioFile_IsMarkedProcessed()
    {
        var missingPath = Path.Combine(CreateTempDir(), "gone.m4a");
        _db.UserFeedbacks.Add(new UserFeedback
        {
            ReceivedAt = DateTime.UtcNow,
            AudioFilePath = missingPath
        });
        await _db.SaveChangesAsync();

        var service = CreateService();
        await service.RunPassAsync(CancellationToken.None);

        var row = await _db.UserFeedbacks.SingleAsync();
        Assert.Equal("[audio file missing]", row.Transcript);
        Assert.NotNull(row.ProcessedAt);
    }

    [Fact]
    public async Task RunPass_RespectsBatchSize()
    {
        for (var i = 0; i < 4; i++)
        {
            _db.UserFeedbacks.Add(new UserFeedback { ReceivedAt = DateTime.UtcNow });
        }
        await _db.SaveChangesAsync();

        var service = CreateService(w => w.BatchSize = 2);
        await service.RunPassAsync(CancellationToken.None);

        var processed = await _db.UserFeedbacks.CountAsync(f => f.ProcessedAt != null);
        Assert.Equal(2, processed);
    }
}
