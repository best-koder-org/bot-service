using BotService.Models;
using BotService.Services.Llm;

namespace BotService.Tests.Services;

public class PromptTemplatesTests
{
    private static BotPersona CreateTestPersona(string chattiness = "medium") => new()
    {
        Id = "test-sofia",
        FirstName = "Sofia",
        Age = 28,
        City = "Stockholm",
        Occupation = "Grafisk designer",
        Interests = new List<string> { "fotografi", "yoga", "matlagning", "resor", "konst", "film" },
        Behavior = new BotBehavior { Chattiness = chattiness }
    };

    // ── Stage detection ──────────────────────────────────────────────

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
    public void DetectStage_MessageCount_ReturnsCorrectStage(int messageCount, string expectedStage)
    {
        Assert.Equal(expectedStage, PromptTemplates.DetectStage(messageCount));
    }

    // ── System prompt contains persona identity ─────────────────────

    [Fact]
    public void BuildSystemPrompt_ContainsPersonaName()
    {
        var persona = CreateTestPersona();
        var prompt = PromptTemplates.BuildSystemPrompt(persona, "intro");
        Assert.Contains("Sofia", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsPersonaAge()
    {
        var persona = CreateTestPersona();
        var prompt = PromptTemplates.BuildSystemPrompt(persona, "intro");
        Assert.Contains("28", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsCity()
    {
        var persona = CreateTestPersona();
        var prompt = PromptTemplates.BuildSystemPrompt(persona, "intro");
        Assert.Contains("Stockholm", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsOccupation()
    {
        var persona = CreateTestPersona();
        var prompt = PromptTemplates.BuildSystemPrompt(persona, "intro");
        Assert.Contains("Grafisk designer", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsInterests_UpToFive()
    {
        var persona = CreateTestPersona();
        var prompt = PromptTemplates.BuildSystemPrompt(persona, "intro");
        Assert.Contains("fotografi", prompt);
        Assert.Contains("yoga", prompt);
        Assert.Contains("matlagning", prompt);
        Assert.Contains("resor", prompt);
        Assert.Contains("konst", prompt);
        // 6th interest should be excluded (Take(5))
        Assert.DoesNotContain("film", prompt);
    }

    // ── Swedish language requirements in prompt ─────────────────────

    [Fact]
    public void BuildSystemPrompt_ContainsSwedishLanguageInstruction()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona(), "intro");
        Assert.Contains("svenska", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsMaxLengthRule()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona(), "intro");
        Assert.Contains("Max 2 meningar", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsNoBotRevealRule()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona(), "intro");
        Assert.Contains("AI", prompt);
        Assert.Contains("bot", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsNoContactInfoRule()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona(), "intro");
        Assert.Contains("telefonnummer", prompt);
        Assert.Contains("URL", prompt);
    }

    // ── Chattiness calibration ──────────────────────────────────────

    [Fact]
    public void BuildSystemPrompt_ChattinessLow_ContainsQuietDescription()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona("low"), "intro");
        Assert.Contains("tystlåten", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ChattinessHigh_ContainsSocialDescription()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona("high"), "intro");
        Assert.Contains("social", prompt);
        Assert.Contains("följdfrågor", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ChattinessMedium_ContainsNormalDescription()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona("medium"), "intro");
        Assert.Contains("normalt", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ChattinessUnknown_FallsbackToMedium()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona("super_chatty"), "intro");
        // Unknown chattiness → default branch (same as medium)
        Assert.Contains("normalt", prompt);
    }

    // ── Stage instructions in prompt ────────────────────────────────

    [Fact]
    public void BuildSystemPrompt_IntroStage_ContainsGreetingInstruction()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona(), "intro");
        Assert.Contains("matchat", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_GettingToKnowStage_ContainsInterestDiscovery()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona(), "getting_to_know");
        Assert.Contains("intressen", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_DeepTalkStage_ContainsDeeperConversation()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona(), "deep_talk");
        Assert.Contains("djupare", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_SuggestFikaStage_ContainsFikaSuggestion()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona(), "suggest_fika");
        Assert.Contains("fika", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_PostFikaStage_ContainsEnthusiasm()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona(), "post_fika");
        Assert.Contains("entusiastisk", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_UnknownStage_ContainsGenericContinuation()
    {
        var prompt = PromptTemplates.BuildSystemPrompt(CreateTestPersona(), "unknown_stage");
        Assert.Contains("naturligt", prompt);
    }

    // ── All 5 valid stages produce different prompts ────────────────

    [Fact]
    public void BuildSystemPrompt_AllStagesProduceDistinctInstructions()
    {
        var persona = CreateTestPersona();
        var stages = new[] { "intro", "getting_to_know", "deep_talk", "suggest_fika", "post_fika" };
        var prompts = stages.Select(s => PromptTemplates.BuildSystemPrompt(persona, s)).ToList();

        // Each stage should produce a unique prompt
        Assert.Equal(stages.Length, prompts.Distinct().Count());
    }
}
