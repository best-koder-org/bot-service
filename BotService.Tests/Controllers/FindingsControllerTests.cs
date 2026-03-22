using BotService.Controllers;
using BotService.Data;
using BotService.Models;
using BotService.Services.Observer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotService.Tests.Controllers;

public class FindingsControllerTests : IDisposable
{
    private readonly BotDbContext _db;
    private readonly FindingsController _controller;
    private readonly BotObserver _observer;

    public FindingsControllerTests()
    {
        var options = new DbContextOptionsBuilder<BotDbContext>()
            .UseInMemoryDatabase($"FindingsTests_{Guid.NewGuid()}")
            .Options;
        _db = new BotDbContext(options);

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddScoped(_ => _db);
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        _observer = new BotObserver(sp, sp.GetRequiredService<ILoggerFactory>().CreateLogger<BotObserver>());
        _controller = new FindingsController(_observer, _db, new Mock<ILogger<FindingsController>>().Object);
    }

    public void Dispose() => _db.Dispose();

    // ── GetFindings ──

    [Fact]
    public async Task GetFindings_Empty_ReturnsEmptyList()
    {
        var result = await _controller.GetFindings(null, null, null, null);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task GetFindings_WithData_ReturnsPagedResults()
    {
        SeedFindings(3);
        var result = await _controller.GetFindings(null, null, null, null, page: 1, pageSize: 2);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task GetFindings_FilterByType_ReturnsOnlyMatching()
    {
        _db.BotFindings.AddRange(
            new BotFinding { Type = FindingType.ServerError, Title = "500" },
            new BotFinding { Type = FindingType.SlowResponse, Title = "slow" }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.GetFindings(type: FindingType.ServerError, severity: null, service: null, unresolved: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("500", json);
        Assert.DoesNotContain("slow", json);
    }

    [Fact]
    public async Task GetFindings_FilterBySeverity_ReturnsOnlyMatching()
    {
        _db.BotFindings.AddRange(
            new BotFinding { Severity = FindingSeverity.Critical, Title = "critical" },
            new BotFinding { Severity = FindingSeverity.Info, Title = "info" }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.GetFindings(type: null, severity: FindingSeverity.Critical, service: null, unresolved: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("critical", json);
    }

    [Fact]
    public async Task GetFindings_FilterByService_ReturnsOnlyMatching()
    {
        _db.BotFindings.AddRange(
            new BotFinding { AffectedService = "UserService", Title = "user" },
            new BotFinding { AffectedService = "PhotoService", Title = "photo" }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.GetFindings(type: null, severity: null, service: "UserService", unresolved: null);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("user", json);
        Assert.DoesNotContain("photo", json);
    }

    [Fact]
    public async Task GetFindings_FilterUnresolved_ReturnsOnlyOpen()
    {
        _db.BotFindings.AddRange(
            new BotFinding { Title = "open", IsResolved = false },
            new BotFinding { Title = "closed", IsResolved = true }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.GetFindings(type: null, severity: null, service: null, unresolved: true);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("open", json);
        Assert.DoesNotContain("closed", json);
    }

    // ── GetFinding ──

    [Fact]
    public async Task GetFinding_ExistingId_ReturnsOk()
    {
        var f = new BotFinding { Title = "test" };
        _db.BotFindings.Add(f);
        await _db.SaveChangesAsync();

        var result = await _controller.GetFinding(f.Id);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetFinding_NonExistingId_ReturnsNotFound()
    {
        var result = await _controller.GetFinding(999);
        Assert.IsType<NotFoundResult>(result);
    }

    // ── ResolveFinding ──

    [Fact]
    public async Task ResolveFinding_ExistingId_MarksResolved()
    {
        var f = new BotFinding { Title = "bug", IsResolved = false };
        _db.BotFindings.Add(f);
        await _db.SaveChangesAsync();

        var result = await _controller.ResolveFinding(f.Id, new ResolveRequest { Notes = "fixed in v2" });
        var ok = Assert.IsType<OkObjectResult>(result);
        var resolved = Assert.IsType<BotFinding>(ok.Value);
        Assert.True(resolved.IsResolved);
        Assert.Equal("fixed in v2", resolved.ResolutionNotes);
        Assert.NotNull(resolved.ResolvedAt);
    }

    [Fact]
    public async Task ResolveFinding_NonExisting_ReturnsNotFound()
    {
        var result = await _controller.ResolveFinding(999, new ResolveRequest { Notes = "n/a" });
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Export ──

    [Fact]
    public async Task ExportFindings_Json_ReturnsJsonWithCount()
    {
        SeedFindings(5);
        var result = await _controller.ExportFindings(format: "json");
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"count\":5", json);
    }

    [Fact]
    public async Task ExportFindings_Csv_ReturnsFileResult()
    {
        SeedFindings(3);
        var result = await _controller.ExportFindings(format: "csv");
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        Assert.StartsWith("findings-", file.FileDownloadName);
        var content = System.Text.Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("Id,FoundAt,Type,Severity", content); // header
        Assert.Equal(4, content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // header + 3 rows
    }

    [Fact]
    public async Task ExportFindings_FilterByType_ExportsOnlyMatching()
    {
        _db.BotFindings.AddRange(
            new BotFinding { Type = FindingType.Timeout, Title = "timeout1" },
            new BotFinding { Type = FindingType.ServerError, Title = "err1" }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.ExportFindings(format: "json", type: FindingType.Timeout);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"count\":1", json);
        Assert.Contains("timeout1", json);
    }

    [Fact]
    public async Task ExportFindings_FilterByHoursBack_ExportsRecent()
    {
        _db.BotFindings.AddRange(
            new BotFinding { Title = "recent", FoundAt = DateTime.UtcNow.AddMinutes(-30) },
            new BotFinding { Title = "old", FoundAt = DateTime.UtcNow.AddHours(-48) }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.ExportFindings(format: "json", hoursBack: 2);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("recent", json);
        Assert.DoesNotContain("old", json);
    }

    // ── GetSummary ──

    [Fact]
    public async Task GetSummary_ReturnsOk()
    {
        SeedFindings(2);
        var result = await _controller.GetSummary(hoursBack: null);
        Assert.IsType<OkObjectResult>(result);
    }

    // ── GetRecent ──

    [Fact]
    public async Task GetRecent_ReturnsOk()
    {
        var result = _controller.GetRecent(count: 5);
        Assert.IsType<OkObjectResult>(result);
    }

    // ── helpers ──

    private void SeedFindings(int count)
    {
        for (var i = 0; i < count; i++)
            _db.BotFindings.Add(new BotFinding
            {
                Title = $"Finding {i}",
                Type = FindingType.ServerError,
                Severity = FindingSeverity.Medium,
                AffectedService = "TestService",
                BotPersona = "bot1"
            });
        _db.SaveChanges();
    }
}
