using BotService.Controllers;
using BotService.Data;
using BotService.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;

namespace BotService.Tests.Controllers;

public class UserFeedbackControllerTests : IDisposable
{
    private readonly BotDbContext _db;
    private readonly UserFeedbackController _controller;
    private readonly string _tmpRoot;

    public UserFeedbackControllerTests()
    {
        var options = new DbContextOptionsBuilder<BotDbContext>()
            .UseInMemoryDatabase($"UserFeedbackTests_{Guid.NewGuid()}")
            .Options;
        _db = new BotDbContext(options);

        _tmpRoot = Path.Combine(Path.GetTempPath(), $"botfb_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpRoot);

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(_tmpRoot);
        env.SetupGet(e => e.EnvironmentName).Returns("Development");

        _controller = new UserFeedbackController(_db, new Mock<ILogger<UserFeedbackController>>().Object, env.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    private static IFormFile MakeAudio(string name = "memo.m4a", byte[]? bytes = null)
    {
        bytes ??= Encoding.UTF8.GetBytes("fake-audio-bytes");
        var ms = new MemoryStream(bytes);
        return new FormFile(ms, 0, bytes.Length, "audio", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/aac"
        };
    }

    [Fact]
    public async Task Submit_AudioOnly_Persists_ReturnsCreated()
    {
        var result = await _controller.Submit(new UserFeedbackSubmission
        {
            Audio = MakeAudio(),
            DurationSec = 5,
            Screen = "matches",
            AppVersion = "0.2.0",
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.NotNull(created.Value);

        var row = await _db.UserFeedbacks.SingleAsync();
        Assert.NotNull(row.AudioFilePath);
        Assert.True(File.Exists(row.AudioFilePath));
        Assert.Equal(5, row.DurationSec);
        Assert.Equal("matches", row.Screen);
        Assert.Equal("0.2.0", row.AppVersion);
        Assert.Null(row.SubmitterKeycloakId);
    }

    [Fact]
    public async Task Submit_NoteOnly_NoAudio_Succeeds()
    {
        var result = await _controller.Submit(new UserFeedbackSubmission
        {
            NoteText = "the discover button is hard to find",
            Screen = "home",
        });

        Assert.IsType<CreatedAtActionResult>(result);
        var row = await _db.UserFeedbacks.SingleAsync();
        Assert.Null(row.AudioFilePath);
        Assert.Equal("the discover button is hard to find", row.NoteText);
    }

    [Fact]
    public async Task Submit_EmptyPayload_ReturnsBadRequest()
    {
        var result = await _controller.Submit(new UserFeedbackSubmission());
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Submit_UnsupportedExtension_ReturnsBadRequest()
    {
        var result = await _controller.Submit(new UserFeedbackSubmission
        {
            Audio = MakeAudio(name: "memo.exe"),
        });
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_db.UserFeedbacks);
    }

    [Fact]
    public async Task List_FiltersUnprocessed()
    {
        _db.UserFeedbacks.AddRange(
            new UserFeedback { NoteText = "a" },
            new UserFeedback { NoteText = "b", Transcript = "done", ProcessedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.List(unprocessed: true, since: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"a\"", json);
        Assert.DoesNotContain("\"b\"", json);
    }

    [Fact]
    public async Task PatchTranscript_UpdatesAndMarksProcessed()
    {
        var row = new UserFeedback { NoteText = "x" };
        _db.UserFeedbacks.Add(row);
        await _db.SaveChangesAsync();

        var result = await _controller.PatchTranscript(row.Id, new TranscriptUpdate { Transcript = "Hello world" });
        Assert.IsType<OkObjectResult>(result);

        var updated = await _db.UserFeedbacks.FindAsync(row.Id);
        Assert.Equal("Hello world", updated!.Transcript);
        Assert.NotNull(updated.ProcessedAt);
    }

    [Fact]
    public async Task GetAudio_MissingFile_ReturnsNotFound()
    {
        var row = new UserFeedback { AudioFilePath = "/nonexistent/file.m4a" };
        _db.UserFeedbacks.Add(row);
        await _db.SaveChangesAsync();

        var result = await _controller.GetAudio(row.Id);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetAudio_ExistingFile_ReturnsFileStream()
    {
        // submit first so file is created
        await _controller.Submit(new UserFeedbackSubmission { Audio = MakeAudio() });
        var row = await _db.UserFeedbacks.SingleAsync();

        var result = await _controller.GetAudio(row.Id);
        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("audio/aac", file.ContentType);
    }

    [Fact]
    public async Task List_FiltersAndReturnsUnprocessed()
    {
        _db.UserFeedbacks.AddRange(
            new UserFeedback { Id = 0, NoteText = "unprocessed-1" },
            new UserFeedback { Id = 0, NoteText = "processed", Transcript = "done", ProcessedAt = DateTime.UtcNow },
            new UserFeedback { Id = 0, NoteText = "unprocessed-2" }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.List(unprocessed: true, since: null, page: 1, pageSize: 50);
        var ok = Assert.IsType<OkObjectResult>(result);

        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("unprocessed-1", json);
        Assert.Contains("unprocessed-2", json);
        Assert.DoesNotContain("noteText\":\"processed\"", json);
    }

    [Fact]
    public async Task PatchTranscript_SimulatesWatcherPipeline()
    {
        // Step 1: Submit audio (as the app would)
        var submitResult = await _controller.Submit(new UserFeedbackSubmission
        {
            Audio = MakeAudio(),
            DurationSec = 3,
            Screen = "matches",
            AppVersion = "test",
        });
        var created = Assert.IsType<CreatedAtActionResult>(submitResult);
        var dto = created.Value!;

        // Extract the id via reflection (anonymous type)
        var idProp = dto.GetType().GetProperty("id");
        Assert.NotNull(idProp);
        var id = Assert.IsType<int>(idProp.GetValue(dto));

        // Step 2: GET unprocessed list (as the watcher would)
        var listResult = await _controller.List(unprocessed: true, since: null, page: 1, pageSize: 50);
        var listOk = Assert.IsType<OkObjectResult>(listResult);
        var listJson = System.Text.Json.JsonSerializer.Serialize(listOk.Value);
        Assert.Contains($"\"id\":{id}", listJson);

        // Step 3: PATCH transcript (as the watcher would)
        var patchResult = await _controller.PatchTranscript(id, new TranscriptUpdate { Transcript = "Hello from watcher test" });
        Assert.IsType<OkObjectResult>(patchResult);

        // Step 4: Verify row is now processed
        var updated = await _db.UserFeedbacks.FindAsync(id);
        Assert.NotNull(updated);
        Assert.Equal("Hello from watcher test", updated.Transcript);
        Assert.NotNull(updated.ProcessedAt);
    }
}
