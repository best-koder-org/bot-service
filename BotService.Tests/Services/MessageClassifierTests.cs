using BotService.Services.Conversation;
using static BotService.Services.Conversation.MessageClassifier;

namespace BotService.Tests.Services;

public class MessageClassifierTests
{
    // ── Normal messages ────────────────────────────────────────

    [Theory]
    [InlineData("Hej! Hur mår du idag?")]
    [InlineData("Jag gillar att vandra i bergen")]
    [InlineData("Vad jobbar du med?")]
    [InlineData("Det låter jättekul, berätta mer!")]
    public void Classify_NormalMessage_ReturnsNormal(string message) =>
        Assert.Equal(MessageTone.Normal, Classify(message));

    // ── Empty/null → Cold ──────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_EmptyOrNull_ReturnsCold(string? message) =>
        Assert.Equal(MessageTone.Cold, Classify(message!));

    [Theory]
    [InlineData("ok")]
    [InlineData("nej")]
    [InlineData("ja")]
    public void Classify_VeryShortMessage_ReturnsCold(string message) =>
        Assert.Equal(MessageTone.Cold, Classify(message));

    // ── Cold keywords ──────────────────────────────────────────

    [Theory]
    [InlineData("Nej tack, inte intresserad")]
    [InlineData("Sluta skriv till mig")]
    [InlineData("Lämna mig ifred")]
    [InlineData("Adjö, hej då")]
    [InlineData("Orkar inte med detta")]
    public void Classify_ColdKeywords_ReturnsCold(string message) =>
        Assert.Equal(MessageTone.Cold, Classify(message));

    // ── Flirty keywords ────────────────────────────────────────

    [Theory]
    [InlineData("Du är väldigt snygg!")]
    [InlineData("Jag vill träffa dig")]
    [InlineData("Kram ❤")]
    [InlineData("Du är underbar")]
    [InlineData("Jag längtar efter att ses")]
    [InlineData("😍 fantastisk bild!")]
    public void Classify_FlirtyKeywords_ReturnsFlirty(string message) =>
        Assert.Equal(MessageTone.Flirty, Classify(message));

    // ── Sexual keywords ────────────────────────────────────────

    [Theory]
    [InlineData("Skicka nakenbilder")]
    [InlineData("Vill du ha sex?")]
    [InlineData("Kolla min onlyfans")]
    [InlineData("Visa dig för mig")]
    public void Classify_SexualKeywords_ReturnsSexual(string message) =>
        Assert.Equal(MessageTone.Sexual, Classify(message));

    // ── Spam keywords ──────────────────────────────────────────

    [Theory]
    [InlineData("Investera i bitcoin nu!")]
    [InlineData("Du har vunnit 10000kr")]
    [InlineData("Kolla min länk: www.sketchy.com")]
    [InlineData("Swisha mig 500kr")]
    [InlineData("Klicka här https://spam.se/")]
    public void Classify_SpamKeywords_ReturnsSpam(string message) =>
        Assert.Equal(MessageTone.Spam, Classify(message));

    // ── Suspicious keywords ────────────────────────────────────

    [Theory]
    [InlineData("Ge mig ditt nummer")]
    [InlineData("Lägg till mig på snapchat")]
    [InlineData("Skriv till mig på whatsapp istället")]
    [InlineData("Flytta till telegram")]
    [InlineData("Ange ditt kort")]
    public void Classify_SuspiciousKeywords_ReturnsSuspicious(string message) =>
        Assert.Equal(MessageTone.Suspicious, Classify(message));

    // ── Priority ordering: sexual > spam > suspicious > flirty > cold ──

    [Fact]
    public void Classify_SexualTrumpsFlirty()
    {
        // "snygg" is flirty, "naken" is sexual → sexual wins
        Assert.Equal(MessageTone.Sexual, Classify("Du är snygg, skicka nakenbilder"));
    }

    [Fact]
    public void Classify_SpamTrumpsSuspicious()
    {
        // "telegram" is suspicious, "bitcoin" is spam → spam wins  
        Assert.Equal(MessageTone.Spam, Classify("Investera i bitcoin, kontakta mig på telegram"));
    }

    // ── Case insensitivity ─────────────────────────────────────

    [Fact]
    public void Classify_IsCaseInsensitive()
    {
        Assert.Equal(MessageTone.Sexual, Classify("NAKENBILDER"));
        Assert.Equal(MessageTone.Spam, Classify("BITCOIN"));
        Assert.Equal(MessageTone.Suspicious, Classify("SNAPCHAT"));
        Assert.Equal(MessageTone.Flirty, Classify("FANTASTISK, du är jättebra!"));
    }

    // ── Safety relevance ───────────────────────────────────────

    [Theory]
    [InlineData(MessageTone.Sexual, true)]
    [InlineData(MessageTone.Spam, true)]
    [InlineData(MessageTone.Suspicious, true)]
    [InlineData(MessageTone.Normal, false)]
    [InlineData(MessageTone.Flirty, false)]
    [InlineData(MessageTone.Cold, false)]
    public void IsSafetyRelevant_CorrectForEachTone(MessageTone tone, bool expected) =>
        Assert.Equal(expected, MessageClassifier.IsSafetyRelevant(tone));
}
