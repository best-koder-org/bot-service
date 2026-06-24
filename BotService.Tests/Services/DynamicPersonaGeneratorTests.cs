using System.Reflection;
using BotService.Models;
using BotService.Services;
using Xunit;

namespace BotService.Tests.Services;

/// <summary>
/// T364 — Tests for DynamicPersonaGenerator's persona parsing logic.
/// </summary>
public class DynamicPersonaGeneratorTests
{
    private static readonly MethodInfo? ParseMethod = typeof(DynamicPersonaGenerator)
        .GetMethod("ParsePersona", BindingFlags.NonPublic | BindingFlags.Static);

    private static BotPersona? InvokeParse(string json, string gender, string city)
    {
        return ParseMethod?.Invoke(null, new object[] { json, gender, city }) as BotPersona;
    }

    [Fact]
    public void ParsePersona_ValidJson_ReturnsPersona()
    {
        var json = @"{""id"":""bot_test-a"",""firstName"":""Test"",""lastName"":""Andersson"",""age"":28,""gender"":""female"",""city"":""Malmö"",""occupation"":""Lärare"",""bio"":""En glad lärare.""}";
        var result = InvokeParse(json, "female", "Malmö");

        Assert.NotNull(result);
        Assert.Equal("bot_test-a", result!.Id);
        Assert.Equal("Test", result.FirstName);
        Assert.Equal(28, result.Age);
        Assert.Equal("Malmö", result.City);
        Assert.Contains("Svenska", result.Languages);
    }

    [Fact]
    public void ParsePersona_ExtractsInterests()
    {
        var json = @"{""id"":""bot_p-s"",""firstName"":""test"",""interests"":[""hundar"",""mat"",""resor""]}";
        var result = InvokeParse(json, "female", "Stockholm");

        Assert.NotNull(result);
        Assert.Contains("hundar", result!.Interests);
        Assert.Contains("resor", result.Interests);
        Assert.Equal(3, result.Interests.Count);
    }

    [Fact]
    public void ParsePersona_HandlesMarkdownWrappedJson()
    {
        var json = "```json\n{\"id\":\"bot_markdown\",\"firstName\":\"Mark\",\"lastName\":\"Down\"}\n```";
        var result = InvokeParse(json, "male", "Göteborg");

        Assert.NotNull(result);
        Assert.Equal("bot_markdown", result!.Id);
        Assert.Equal("Mark", result.FirstName);
    }

    [Fact]
    public void ParsePersona_InvalidJson_ReturnsNull()
    {
        var result = InvokeParse("not json at all", "female", "Stockholm");
        Assert.Null(result);
    }

    [Fact]
    public void ParsePersona_AssignsGenderAndCityFromArgs()
    {
        var json = @"{""id"":""bot_x-y"",""firstName"":""X"",""lastName"":""Y""}";
        var result = InvokeParse(json, "non-binary", "Uppsala");

        Assert.NotNull(result);
        Assert.Equal("non-binary", result!.Gender);
        Assert.Equal("Uppsala", result.City);
    }
}
