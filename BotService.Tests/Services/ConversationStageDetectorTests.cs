using BotService.Services.Conversation;

namespace BotService.Tests.Services;

public class ConversationStageDetectorTests
{
    // ── Count-based fallback (same as PromptTemplates.DetectStage) ──

    [Theory]
    [InlineData(0, "intro")]
    [InlineData(1, "intro")]
    [InlineData(2, "intro")]
    [InlineData(3, "getting_to_know")]
    [InlineData(8, "getting_to_know")]
    [InlineData(9, "deep_talk")]
    [InlineData(15, "deep_talk")]
    [InlineData(16, "suggest_fika")]
    [InlineData(20, "suggest_fika")]
    [InlineData(21, "post_fika")]
    [InlineData(100, "post_fika")]
    public void Detect_NoContent_FallsBackToMessageCount(int count, string expected)
    {
        var result = ConversationStageDetector.Detect(count);
        Assert.Equal(expected, result.Stage);
        Assert.Equal("message_count", result.Reason);
    }

    [Fact]
    public void Detect_NullMessages_FallsBackToMessageCount()
    {
        var result = ConversationStageDetector.Detect(5, null);
        Assert.Equal("getting_to_know", result.Stage);
        Assert.Equal("message_count", result.Reason);
    }

    [Fact]
    public void Detect_EmptyMessages_FallsBackToMessageCount()
    {
        var result = ConversationStageDetector.Detect(5, new List<string>());
        Assert.Equal("getting_to_know", result.Stage);
        Assert.Equal("message_count", result.Reason);
    }

    // ── Fika mention acceleration ──────────────────────────────

    [Theory]
    [InlineData("Ska vi fika nån gång?")]
    [InlineData("Vill du gå på kaffe?")]
    [InlineData("Vi borde träffas!")]
    [InlineData("Kan vi ses snart?")]
    [InlineData("Vi kanske borde gå på en dejt?")]
    public void Detect_FikaMention_AcceleratesToSuggestFika(string message)
    {
        var result = ConversationStageDetector.Detect(5, new List<string> { message });
        Assert.Equal("suggest_fika", result.Stage);
        Assert.Equal("fika_mentioned", result.Reason);
    }

    [Fact]
    public void Detect_FikaMention_RequiresMinMessages()
    {
        // At messageCount=3 (below 4), fika mention should NOT accelerate
        var result = ConversationStageDetector.Detect(3, new List<string> { "Vi borde fika!" });
        Assert.Equal("getting_to_know", result.Stage);
        Assert.Equal("message_count", result.Reason);
    }

    // ── Fika confirmation acceleration ─────────────────────────

    [Theory]
    [InlineData("Vi ses på lördag!")]
    [InlineData("Absolut, perfekt!")]
    [InlineData("Ser fram emot det")]
    [InlineData("Vilken tid passar dig?")]
    [InlineData("Jag kommer!")]
    public void Detect_FikaConfirmation_AcceleratesToPostFika(string message)
    {
        var result = ConversationStageDetector.Detect(7, new List<string> { message });
        Assert.Equal("post_fika", result.Stage);
        Assert.Equal("fika_confirmed", result.Reason);
    }

    [Fact]
    public void Detect_FikaConfirmation_RequiresMinMessages()
    {
        // At messageCount=5 (below 6), confirmation should NOT accelerate to post_fika
        // But fika mention can still trigger at messageCount >= 4
        var result = ConversationStageDetector.Detect(5, new List<string> { "Vi ses på lördag!" });
        // "ses" matches fika_mention, so should be suggest_fika at count 5
        Assert.Equal("suggest_fika", result.Stage);
    }

    // ── Question density acceleration ──────────────────────────

    [Fact]
    public void Detect_HighQuestionDensity_AcceleratesToDeepTalk()
    {
        var messages = new List<string>
        {
            "Vad jobbar du med?",
            "Vad gör du på fritiden?",
            "Vad drömmer du om?",
            "Jag jobbar som lärare"
        };
        var result = ConversationStageDetector.Detect(6, messages);
        Assert.Equal("deep_talk", result.Stage);
        Assert.Equal("high_question_density", result.Reason);
    }

    [Fact]
    public void Detect_LowQuestionDensity_NoAcceleration()
    {
        var messages = new List<string>
        {
            "Hej!",
            "Tjena, allt bra?",
            "Ja det är bra",
            "Vad gör du?" // only 1 question
        };
        var result = ConversationStageDetector.Detect(6, messages);
        Assert.Equal("getting_to_know", result.Stage);
        Assert.Equal("message_count", result.Reason);
    }

    [Fact]
    public void Detect_QuestionDensity_OnlyInRange5to10()
    {
        var messages = new List<string>
        {
            "Vad?", "Varför?", "Hur?"
        };
        // At messageCount=12, question density check doesn't apply (>10)
        var result = ConversationStageDetector.Detect(12, messages);
        Assert.Equal("deep_talk", result.Stage);
        Assert.Equal("message_count", result.Reason);
    }

    // ── Priority: fika_confirmed > fika_mentioned > question_density ──

    [Fact]
    public void Detect_FikaConfirmation_TrumpsFikaMention()
    {
        // "Absolut, vi ses på fika!" has both mention and confirmation keywords
        var result = ConversationStageDetector.Detect(7, 
            new List<string> { "Absolut, vi ses på fika!" });
        Assert.Equal("post_fika", result.Stage);
        Assert.Equal("fika_confirmed", result.Reason);
    }

    // ── StageResult record ─────────────────────────────────────

    [Fact]
    public void StageResult_HasExpectedProperties()
    {
        var result = new ConversationStageDetector.StageResult("intro", "test");
        Assert.Equal("intro", result.Stage);
        Assert.Equal("test", result.Reason);
    }
}
