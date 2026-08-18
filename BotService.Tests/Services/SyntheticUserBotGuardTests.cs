using BotService.Models;
using BotService.Services.BotModes;

namespace BotService.Tests.Services;

/// <summary>
/// Tests the bot↔bot skip guard in <see cref="SyntheticUserService"/>.
/// The demo-user persona is the human test account, so bots must be allowed
/// to reply to it (i.e. it must NOT be treated as "another bot" to skip).
/// </summary>
public class SyntheticUserBotGuardTests
{
    [Theory]
    [InlineData("demo-user", "demo-kc", false)]  // human test account → NOT skipped
    [InlineData("maja", "maja-kc", true)]        // another bot persona → skipped
    [InlineData("linnea", "linnea-kc", true)]    // another bot persona → skipped
    public void IsBotTargetExcluded_AppliesDemoUserException(
        string personaId, string keycloakId, bool expected)
    {
        var state = new BotState { PersonaId = personaId, KeycloakUserId = keycloakId };
        Assert.Equal(expected, SyntheticUserService.IsBotTargetExcluded(state));
    }
}
