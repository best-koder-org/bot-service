using BotService.Data;
using BotService.Models;
using BotService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotService.Tests.Services;

/// <summary>
/// T365 — Tests for BehaviorLearningService.
/// </summary>
public class BehaviorLearningTests : IDisposable
{
    private readonly BotDbContext _db;
    private readonly BehaviorLearningService _svc;
    private readonly BotMetrics _metrics;
    private readonly BotPersonaEngine _engine;

    public BehaviorLearningTests()
    {
        var opts = new DbContextOptionsBuilder<BotDbContext>()
            .UseInMemoryDatabase($"BehaviorTests_{Guid.NewGuid()}")
            .Options;
        _db = new BotDbContext(opts);
        _metrics = new BotMetrics();
        _engine = new BotPersonaEngine(NullLogger<BotPersonaEngine>.Instance);
        _svc = new BehaviorLearningService(_db, _engine, _metrics, NullLogger<BehaviorLearningService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task LearnAsync_NoBots_DoesNothing()
    {
        await _svc.LearnAsync();
        Assert.Equal(0, _metrics.GetCounter("bot_behavior_adjustments_total"));
    }

    [Fact]
    public async Task LearnAsync_BotWithHighMatchRate_IncreasesSwipeProb()
    {
        var persona = new BotPersona { Id = "bot_test", FirstName = "Test", Modes = new() { "synthetic" } };
        _engine.AddPersona(persona);

        _db.BotStates.Add(new BotState
        {
            PersonaId = "bot_test",
            Status = BotStatus.Active,
            SwipesToday = 10,
            MatchCount = 3,
            MessagesSentToday = 5,
        });
        await _db.SaveChangesAsync();

        await _svc.LearnAsync();

        Assert.True(persona.Behavior.SwipeRightProbability > 0.4,
            $"Expected > 0.4, got {persona.Behavior.SwipeRightProbability}");
        Assert.True(_metrics.GetCounter("bot_behavior_adjustments_total") > 0);
    }

    [Fact]
    public async Task LearnAsync_BotBelowThreshold_Skips()
    {
        _db.BotStates.Add(new BotState
        {
            PersonaId = "bot_skip",
            Status = BotStatus.Active,
            SwipesToday = 2,
            MatchCount = 0,
        });
        await _db.SaveChangesAsync();

        await _svc.LearnAsync();

        Assert.Equal(0, _metrics.GetCounter("bot_behavior_adjustments_total"));
    }

    [Fact]
    public async Task LearnAsync_LowMatchRate_DecreasesSwipeProb()
    {
        var persona = new BotPersona { Id = "bot_test2", FirstName = "Test2", Modes = new() { "synthetic" } };
        persona.Behavior.SwipeRightProbability = 0.8;
        _engine.AddPersona(persona);

        _db.BotStates.Add(new BotState
        {
            PersonaId = "bot_test2",
            Status = BotStatus.Active,
            SwipesToday = 50,
            MatchCount = 0,
            MessagesSentToday = 10,
        });
        await _db.SaveChangesAsync();

        await _svc.LearnAsync();

        Assert.True(persona.Behavior.SwipeRightProbability < 0.8,
            $"Expected < 0.8, got {persona.Behavior.SwipeRightProbability}");
    }

    [Fact]
    public async Task LearnAsync_HighConversations_IncreasesChattiness()
    {
        var persona = new BotPersona { Id = "bot_chatty", FirstName = "Chatty", Modes = new() { "synthetic" } };
        persona.Behavior.Chattiness = "low";
        _engine.AddPersona(persona);

        _db.BotStates.Add(new BotState
        {
            PersonaId = "bot_chatty",
            Status = BotStatus.Active,
            SwipesToday = 20,
            MatchCount = 5,
            ConversationCount = 8,
            MessagesSentToday = 30,
        });
        await _db.SaveChangesAsync();

        await _svc.LearnAsync();

        Assert.Equal("medium", persona.Behavior.Chattiness);
    }
}
