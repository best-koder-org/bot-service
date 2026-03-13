using System.Text.RegularExpressions;

namespace BotService.Services.Llm;

/// <summary>
/// Post-processing guardrails for LLM output. Rejects unsafe or off-brand responses.
/// Returns null if the response passes all checks, or the rejection reason.
/// </summary>
public static class ResponseGuardrails
{
    private static readonly Regex PhoneRegex = new(@"\d{3,}[\-\s]?\d{3,}", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"(https?://|www\.|\.com|\.se|\.net|\.org)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BotAwarenessRegex = new(
        @"(jag är en (ai|bot|robot|program|maskin|dator)|jag är inte en riktig person|jag är artificiell)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // Common English words that would NOT appear in Swedish
    private static readonly HashSet<string> EnglishOnlyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "is", "are", "was", "were", "have", "has", "been", "would", "could", "should",
        "this", "that", "these", "those", "with", "from", "into", "about", "which", "their", "there",
        "where", "when", "what", "your", "they", "them", "than", "then", "because", "before", "after",
        "between", "through", "during", "without", "however", "although", "though", "while", "since",
        "beautiful", "amazing", "awesome", "wonderful", "sorry", "actually", "basically", "literally"
    };

    /// <summary>Validate an LLM response. Returns null if valid, or rejection reason string.</summary>
    public static string? Validate(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "empty_response";

        // Max length: dating app message style (280 chars)
        if (content.Length > 280)
            return "too_long";

        // Phone numbers
        if (PhoneRegex.IsMatch(content))
            return "contains_phone_number";

        // URLs
        if (UrlRegex.IsMatch(content))
            return "contains_url";

        // Bot awareness leak
        if (BotAwarenessRegex.IsMatch(content))
            return "bot_awareness_leak";

        // English ratio check (>20% English words = reject)
        var englishRatio = CalculateEnglishRatio(content);
        if (englishRatio > 0.20)
            return $"too_much_english ({englishRatio:P0})";

        return null; // All clear
    }

    /// <summary>Calculate what percentage of words are English-only</summary>
    private static double CalculateEnglishRatio(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '!', '?', ':', ';', '"', '\'', '(', ')', '-'))
            .Where(w => w.Length > 2) // Skip short words / emoji
            .ToArray();
        
        if (words.Length == 0) return 0;

        var englishCount = words.Count(w => EnglishOnlyWords.Contains(w));
        return (double)englishCount / words.Length;
    }
}
