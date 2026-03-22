using BotService.Services.Llm;

namespace BotService.Tests.Services;

public class ResponseGuardrailsTests
{
    // ── Valid messages (should pass all checks) ──────────────────────

    [Theory]
    [InlineData("Hej! Hur är läget?")]
    [InlineData("Kul att vi matchade! Vad gillar du att göra på helgerna? 😊")]
    [InlineData("Jag älskar att laga mat, speciellt italienskt.")]
    [InlineData("Ska vi ta en fika nån dag?")]
    [InlineData("Stockholm är fint på sommaren, eller hur?")]
    public void Validate_ValidSwedishMessages_ReturnsNull(string message)
    {
        Assert.Null(ResponseGuardrails.Validate(message));
    }

    // ── Empty / whitespace ──────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Validate_EmptyOrWhitespace_ReturnsEmptyResponse(string? message)
    {
        Assert.Equal("empty_response", ResponseGuardrails.Validate(message!));
    }

    // ── Too long (>280 chars) ───────────────────────────────────────

    [Fact]
    public void Validate_ExactlyAtLimit_Passes()
    {
        // 280 chars of Swedish text
        var msg = new string('å', 280);
        Assert.Null(ResponseGuardrails.Validate(msg));
    }

    [Fact]
    public void Validate_OneOverLimit_ReturnsTooLong()
    {
        var msg = new string('å', 281);
        Assert.Equal("too_long", ResponseGuardrails.Validate(msg));
    }

    [Fact]
    public void Validate_WayOverLimit_ReturnsTooLong()
    {
        var msg = new string('ö', 500);
        Assert.Equal("too_long", ResponseGuardrails.Validate(msg));
    }

    // ── Phone numbers ───────────────────────────────────────────────

    [Theory]
    [InlineData("Ring mig på 070-123 4567")]
    [InlineData("Mitt nummer är 0701234567")]
    [InlineData("Nå mig på 073 456 7890")]
    [InlineData("Skriv till 123-456-7890")]
    [InlineData("Kontakta 08-555 1234")]
    public void Validate_PhoneNumbers_ReturnsContainsPhone(string message)
    {
        Assert.Equal("contains_phone_number", ResponseGuardrails.Validate(message));
    }

    // ── URLs ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Kolla https://example.com")]
    [InlineData("Se min sida www.minprofil.se")]
    [InlineData("Hitta mig på dejting.com")]
    [InlineData("Min Instagram: http://instagram.com/test")]
    [InlineData("Kolla sidan min.se")]
    [InlineData("Besök example.net")]
    [InlineData("Gå till min.org")]
    public void Validate_Urls_ReturnsContainsUrl(string message)
    {
        Assert.Equal("contains_url", ResponseGuardrails.Validate(message));
    }

    // ── Bot awareness leak ──────────────────────────────────────────

    [Theory]
    [InlineData("Jag är en AI faktiskt")]
    [InlineData("jag är en bot som pratar med dig")]
    [InlineData("Haha, jag är en robot!")]
    [InlineData("Jag är en maskin utan känslor")]
    [InlineData("Jag är inte en riktig person")]
    [InlineData("Jag är artificiell intelligens")]
    [InlineData("jag är en program")]
    [InlineData("jag är en dator")]
    public void Validate_BotAwareness_ReturnsBotAwarenessLeak(string message)
    {
        Assert.Equal("bot_awareness_leak", ResponseGuardrails.Validate(message));
    }

    [Fact]
    public void Validate_PartialBotWords_DoNotFalsePositive()
    {
        // "robot" in a non-bot-awareness context should be fine via regex
        // but "jag är en robot" is caught — test for different sentence
        Assert.Null(ResponseGuardrails.Validate("Jag gillar robotfilmer!"));
    }

    // ── English ratio (>20% English-only words = reject) ────────────

    [Fact]
    public void Validate_PureEnglish_ReturnsTooMuchEnglish()
    {
        var msg = "This is an amazing and beautiful message that would be awesome";
        var result = ResponseGuardrails.Validate(msg);
        Assert.NotNull(result);
        Assert.StartsWith("too_much_english", result);
    }

    [Fact]
    public void Validate_MixedButMostlyEnglish_ReturnsTooMuchEnglish()
    {
        // 5 English words out of ~10 total → 50% English
        var msg = "Hej, this is actually basically what jag menar";
        var result = ResponseGuardrails.Validate(msg);
        Assert.NotNull(result);
        Assert.StartsWith("too_much_english", result);
    }

    [Fact]
    public void Validate_MostlySwedishWithFewEnglishLoanwords_Passes()
    {
        // Swedish people use some English loanwords — a couple should be OK below 20%
        var msg = "Jag gillar att spela fotboll och titta på film med min kompis och hund";
        Assert.Null(ResponseGuardrails.Validate(msg));
    }

    [Fact]
    public void Validate_ShortWordsNotCounted_Passes()
    {
        // Words <=2 chars are skipped in the English check
        var msg = "Ja ok vi kan ses på torsdag om du vill";
        Assert.Null(ResponseGuardrails.Validate(msg));
    }

    [Fact]
    public void Validate_EmojisOnly_Passes()
    {
        // Not empty (has visible chars), not English; should pass
        Assert.Null(ResponseGuardrails.Validate("😊❤️🔥"));
    }

    // ── Priority: earlier checks should win when multiple violations ─

    [Fact]
    public void Validate_EmptyBeatsOtherChecks()
    {
        Assert.Equal("empty_response", ResponseGuardrails.Validate(""));
    }

    [Fact]
    public void Validate_TooLongWithUrl_ReturnsTooLong()
    {
        // > 280 chars AND contains URL — too_long should be caught first
        var msg = new string('a', 280) + " https://evil.com";
        Assert.Equal("too_long", ResponseGuardrails.Validate(msg));
    }
}
