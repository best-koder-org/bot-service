using BotService.Controllers;
using BotService.Data;
using BotService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotService.Tests.Controllers;

public class ExperimentsControllerTests : IDisposable
{
    private readonly BotDbContext _db;
    private readonly ExperimentsController _controller;

    public ExperimentsControllerTests()
    {
        var options = new DbContextOptionsBuilder<BotDbContext>()
            .UseInMemoryDatabase($"ExperimentsTests_{Guid.NewGuid()}")
            .Options;
        _db = new BotDbContext(options);
        _controller = new ExperimentsController(_db, new Mock<ILogger<ExperimentsController>>().Object);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_ValidRequest_ReturnsCreatedExperiment()
    {
        var request = new CreateExperimentRequest
        {
            Name = "Opener style test",
            Description = "Compare casual vs formal openers",
            GroupAConfig = "{\"openerStyle\":\"casual\"}",
            GroupBConfig = "{\"openerStyle\":\"formal\"}",
            BotsPerGroup = 10
        };

        var result = await _controller.Create(request);
        var created = Assert.IsType<CreatedAtActionResult>(result);
        var experiment = Assert.IsType<Experiment>(created.Value);

        Assert.Equal("Opener style test", experiment.Name);
        Assert.Equal(ExperimentStatus.Draft, experiment.Status);
        Assert.Equal(10, experiment.BotsPerGroup);
        Assert.True(experiment.Id > 0);
    }

    [Fact]
    public async Task List_ReturnsAllExperiments()
    {
        _db.Experiments.AddRange(
            new Experiment { Name = "Test1", Status = ExperimentStatus.Draft },
            new Experiment { Name = "Test2", Status = ExperimentStatus.Running }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.List();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task List_FilterByStatus_OnlyReturnsMatching()
    {
        _db.Experiments.AddRange(
            new Experiment { Name = "Draft", Status = ExperimentStatus.Draft },
            new Experiment { Name = "Running", Status = ExperimentStatus.Running }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.List(status: ExperimentStatus.Running);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task Get_ExistingId_ReturnsExperiment()
    {
        var exp = new Experiment { Name = "Test" };
        _db.Experiments.Add(exp);
        await _db.SaveChangesAsync();

        var result = await _controller.Get(exp.Id);
        var ok = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<Experiment>(ok.Value);
        Assert.Equal("Test", returned.Name);
    }

    [Fact]
    public async Task Get_NonExistingId_ReturnsNotFound()
    {
        var result = await _controller.Get(999);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Start_DraftExperiment_SetsRunning()
    {
        var exp = new Experiment { Name = "Test", Status = ExperimentStatus.Draft };
        _db.Experiments.Add(exp);
        await _db.SaveChangesAsync();

        var result = await _controller.Start(exp.Id);
        var ok = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<Experiment>(ok.Value);

        Assert.Equal(ExperimentStatus.Running, returned.Status);
        Assert.NotNull(returned.StartedAt);
        Assert.NotNull(returned.EndsAt);
    }

    [Fact]
    public async Task Start_RunningExperiment_ReturnsBadRequest()
    {
        var exp = new Experiment { Name = "Test", Status = ExperimentStatus.Running };
        _db.Experiments.Add(exp);
        await _db.SaveChangesAsync();

        var result = await _controller.Start(exp.Id);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Complete_RunningExperiment_SetsCompleted()
    {
        var exp = new Experiment { Name = "Test", Status = ExperimentStatus.Running };
        _db.Experiments.Add(exp);
        await _db.SaveChangesAsync();

        var result = await _controller.Complete(exp.Id);
        var ok = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<Experiment>(ok.Value);

        Assert.Equal(ExperimentStatus.Completed, returned.Status);
        Assert.NotNull(returned.CompletedAt);
    }

    [Fact]
    public async Task Complete_DraftExperiment_ReturnsBadRequest()
    {
        var exp = new Experiment { Name = "Test", Status = ExperimentStatus.Draft };
        _db.Experiments.Add(exp);
        await _db.SaveChangesAsync();

        var result = await _controller.Complete(exp.Id);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Cancel_AnyExperiment_SetsCancelled()
    {
        var exp = new Experiment { Name = "Test", Status = ExperimentStatus.Running };
        _db.Experiments.Add(exp);
        await _db.SaveChangesAsync();

        var result = await _controller.Cancel(exp.Id);
        var ok = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<Experiment>(ok.Value);

        Assert.Equal(ExperimentStatus.Cancelled, returned.Status);
    }

    [Fact]
    public async Task GetResults_ExistingExperiment_ReturnsResults()
    {
        var exp = new Experiment
        {
            Name = "Test",
            GroupAConfig = "{\"style\":\"casual\"}",
            GroupBConfig = "{\"style\":\"formal\"}",
            Winner = "A"
        };
        _db.Experiments.Add(exp);
        await _db.SaveChangesAsync();

        var result = await _controller.GetResults(exp.Id);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetResults_NonExisting_ReturnsNotFound()
    {
        var result = await _controller.GetResults(999);
        Assert.IsType<NotFoundResult>(result);
    }
}
