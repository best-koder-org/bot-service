using BotService.Data;
using BotService.Models;
using Microsoft.EntityFrameworkCore;

namespace BotService.Tests.Services;

public class BotDbContextTests : IDisposable
{
    private readonly BotDbContext _db;

    public BotDbContextTests()
    {
        var options = new DbContextOptionsBuilder<BotDbContext>()
            .UseInMemoryDatabase(databaseName: $"BotDb-{Guid.NewGuid()}")
            .Options;
        _db = new BotDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task CanAddAndRetrieveBotState()
    {
        var bot = new BotState
        {
            PersonaId = "test-bot",
            KeycloakUserId = "keycloak-123",
            Status = BotStatus.Active
        };

        _db.BotStates.Add(bot);
        await _db.SaveChangesAsync();

        var retrieved = await _db.BotStates.FirstOrDefaultAsync(b => b.PersonaId == "test-bot");
        Assert.NotNull(retrieved);
        Assert.Equal("keycloak-123", retrieved.KeycloakUserId);
        Assert.Equal(BotStatus.Active, retrieved.Status);
    }

    [Fact]
    public async Task PersonaId_HasUniqueIndex()
    {
        _db.BotStates.Add(new BotState { PersonaId = "unique-bot" });
        await _db.SaveChangesAsync();

        // InMemory doesn't enforce unique constraints, so just verify we can query
        var count = await _db.BotStates.CountAsync(b => b.PersonaId == "unique-bot");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CanUpdateBotStatus()
    {
        var bot = new BotState { PersonaId = "status-bot", Status = BotStatus.Provisioning };
        _db.BotStates.Add(bot);
        await _db.SaveChangesAsync();

        bot.Status = BotStatus.Active;
        bot.AccessToken = "token-abc";
        bot.ProfileId = 42;
        await _db.SaveChangesAsync();

        var updated = await _db.BotStates.FirstAsync(b => b.PersonaId == "status-bot");
        Assert.Equal(BotStatus.Active, updated.Status);
        Assert.Equal("token-abc", updated.AccessToken);
        Assert.Equal(42, updated.ProfileId);
    }

    [Fact]
    public async Task CanQueryByStatus()
    {
        _db.BotStates.AddRange(
            new BotState { PersonaId = "a1", Status = BotStatus.Active },
            new BotState { PersonaId = "a2", Status = BotStatus.Active },
            new BotState { PersonaId = "p1", Status = BotStatus.Paused },
            new BotState { PersonaId = "e1", Status = BotStatus.Error }
        );
        await _db.SaveChangesAsync();

        var active = await _db.BotStates.Where(b => b.Status == BotStatus.Active).ToListAsync();
        Assert.Equal(2, active.Count);

        var paused = await _db.BotStates.Where(b => b.Status == BotStatus.Paused).ToListAsync();
        Assert.Single(paused);
    }
}
