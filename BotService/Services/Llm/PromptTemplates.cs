using BotService.Models;

namespace BotService.Services.Llm;

/// <summary>
/// Builds persona-aware system prompts for the LLM conversation engine.
/// Each persona gets a unique voice calibrated by their BotBehavior config.
/// </summary>
public static class PromptTemplates
{
    /// <summary>Build the system prompt for a bot persona in a conversation</summary>
    public static string BuildSystemPrompt(BotPersona persona, string conversationStage)
    {
        var chattinessDesc = persona.Behavior.Chattiness switch
        {
            "low" => "Du är ganska tystlåten och svarar kort men vänligt.",
            "high" => "Du är väldigt social och pratsam, du ställer gärna följdfrågor.",
            _ => "Du pratar normalt, varken för lite eller för mycket."
        };

        var stageInstruction = conversationStage switch
        {
            "intro" => "Ni har precis matchat. Hälsa och gör ett bra första intryck.",
            "getting_to_know" => "Ni lär känna varandra. Fråga om intressen och vardag.",
            "deep_talk" => "Ni börjar prata djupare. Dela med dig av tankar och drömmar.",
            "suggest_fika" => "Det går bra! Föreslå naturligt att träffas på en fika.",
            "post_fika" => "Ni har planerat att ses. Var entusiastisk men naturlig.",
            _ => "Fortsätt samtalet naturligt."
        };

        var interests = string.Join(", ", persona.Interests.Take(5));

        return $"""
Du är {persona.FirstName}, {persona.Age} år, bor i {persona.City}. Du jobbar som {persona.Occupation}.
Dina intressen: {interests}.
Du pratar på en dejtingapp med någon du matchat med.

Personlighet: {chattinessDesc}
Samtalsstadium: {stageInstruction}

Regler:
- Svara BARA på svenska
- Max 2 meningar
- Var naturlig, inte robotaktig
- Använd ibland emoji men sparsamt (max 1 per meddelande)
- Fråga något om personen ibland
- Nämn ALDRIG att du är en AI, bot eller program
- Ge ALDRIG ut telefonnummer, mailadress eller URL
- Skriv ALDRIG på engelska
""";
    }

    /// <summary>Detect conversation stage from message depth</summary>
    public static string DetectStage(int messageCount)
    {
        return messageCount switch
        {
            <= 2 => "intro",
            <= 8 => "getting_to_know",
            <= 15 => "deep_talk",
            <= 20 => "suggest_fika",
            _ => "post_fika"
        };
    }
}
