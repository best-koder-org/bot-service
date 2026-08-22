using BotService.Configuration;
using BotService.Data;
using BotService.Models;
using Microsoft.EntityFrameworkCore;

namespace BotService.Tests.Services;

/// <summary>
/// Tests for Tester Demo Mode: the OnboardingTargets persistence and the
/// runtime-toggleable demo state (reactive fake-user mode).
/// </summary>
public class DemoModeTests : IDisposable
{
    private readonly BotDbContext _db;

    public DemoModeTests()
    {
        var options = new DbContextOptionsBuilder<BotDbContext>()
            .UseInMemoryDatabase(databaseName: $"DemoDb-{Guid.NewGuid()}")
            .Options;
        _db = new BotDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task OnboardingTarget_CanRoundTrip_ThroughDbContext()
    {
        _db.OnboardingTargets.Add(new OnboardingTarget
        {
            KeycloakUserId = "tester-kc-123",
            ProfileId = 77,
            AssistedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var retrieved = await _db.OnboardingTargets
            .FirstOrDefaultAsync(t => t.KeycloakUserId == "tester-kc-123");

        Assert.NotNull(retrieved);
        Assert.Equal(77, retrieved.ProfileId);
    }

    [Fact]
    public async Task OnboardingTarget_CanQueryAssistedSet()
    {
        _db.OnboardingTargets.AddRange(
            new OnboardingTarget { KeycloakUserId = "a", ProfileId = 1 },
            new OnboardingTarget { KeycloakUserId = "b", ProfileId = 2 },
            new OnboardingTarget { KeycloakUserId = "c", ProfileId = 3 });
        await _db.SaveChangesAsync();

        var assisted = await _db.OnboardingTargets
            .Select(t => t.KeycloakUserId)
            .ToListAsync();

        Assert.Contains("a", assisted);
        Assert.Contains("b", assisted);
        Assert.Contains("c", assisted);
        Assert.Equal(3, assisted.Count);
    }

    [Fact]
    public void DemoRuntimeState_DefaultsToReactiveOnly_AndDisabled()
    {
        var state = new DemoRuntimeState();
        Assert.False(state.Enabled);
        Assert.True(state.ReactiveOnly);
    }

    [Fact]
    public void DemoRuntimeState_CanBeToggled()
    {
        var state = new DemoRuntimeState { Enabled = true, ReactiveOnly = true };
        Assert.True(state.Enabled);
        Assert.True(state.ReactiveOnly);

        state.Enabled = false;
        Assert.False(state.Enabled);
    }
}
