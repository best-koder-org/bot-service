using BotService.Models;

namespace BotService.Tests.Services;

public class BotStateTests
{
    [Fact]
    public void ResetDailyCountersIfNeeded_ResetsOnNewDay()
    {
        var bot = new BotState
        {
            SwipesToday = 50,
            MessagesSentToday = 20,
            CounterResetDate = DateTime.UtcNow.Date.AddDays(-1) // Yesterday
        };

        bot.ResetDailyCountersIfNeeded();

        Assert.Equal(0, bot.SwipesToday);
        Assert.Equal(0, bot.MessagesSentToday);
        Assert.Equal(DateTime.UtcNow.Date, bot.CounterResetDate);
    }

    [Fact]
    public void ResetDailyCountersIfNeeded_DoesNotResetSameDay()
    {
        var bot = new BotState
        {
            SwipesToday = 50,
            MessagesSentToday = 20,
            CounterResetDate = DateTime.UtcNow.Date // Today
        };

        bot.ResetDailyCountersIfNeeded();

        Assert.Equal(50, bot.SwipesToday);
        Assert.Equal(20, bot.MessagesSentToday);
    }

    [Fact]
    public void BotState_DefaultValues_AreCorrect()
    {
        var bot = new BotState();

        Assert.Equal(BotStatus.Provisioning, bot.Status);
        Assert.Equal(string.Empty, bot.PersonaId);
        Assert.Equal(string.Empty, bot.KeycloakUserId);
        Assert.Null(bot.ProfileId);
        Assert.Null(bot.AccessToken);
        Assert.Null(bot.RefreshToken);
        Assert.Equal(0, bot.SwipesToday);
        Assert.Equal(0, bot.MessagesSentToday);
        Assert.Equal(0, bot.MatchCount);
        Assert.Equal(0, bot.ConversationCount);
    }

    [Fact]
    public void BotStatus_HasAllExpectedValues()
    {
        var values = Enum.GetValues<BotStatus>();
        Assert.Contains(BotStatus.Provisioning, values);
        Assert.Contains(BotStatus.Active, values);
        Assert.Contains(BotStatus.Idle, values);
        Assert.Contains(BotStatus.Paused, values);
        Assert.Contains(BotStatus.Error, values);
        Assert.Contains(BotStatus.Decommissioned, values);
    }
}
