namespace BotService.Services.Conversation;

/// <summary>
/// Classifies received messages to detect tone and safety signals.
/// Rule-based Swedish text classifier (no LLM calls needed).
/// Fed into BotObserver for safety incident recording.
/// </summary>
public static class MessageClassifier
{
    public enum MessageTone
    {
        Normal,
        Flirty,
        Cold,
        Suspicious,
        Spam,
        Sexual
    }

    /// <summary>Classify a received message's tone</summary>
    public static MessageTone Classify(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return MessageTone.Cold;

        var lower = message.ToLowerInvariant();

        // Sexual — explicit Swedish/English sexual terms
        if (ContainsAny(lower, _sexualKeywords))
            return MessageTone.Sexual;

        // Spam — link patterns, money scams, repeated contact requests
        if (ContainsAny(lower, _spamKeywords))
            return MessageTone.Spam;

        // Suspicious — requests to move off-platform, share personal info
        if (ContainsAny(lower, _suspiciousKeywords))
            return MessageTone.Suspicious;

        // Flirty — positive romantic signals
        if (ContainsAny(lower, _flirtyKeywords))
            return MessageTone.Flirty;

        // Cold — disengagement signals
        if (ContainsAny(lower, _coldKeywords) || message.Trim().Length < 4)
            return MessageTone.Cold;

        return MessageTone.Normal;
    }

    /// <summary>Whether this classification should trigger a safety finding</summary>
    public static bool IsSafetyRelevant(MessageTone tone) =>
        tone is MessageTone.Sexual or MessageTone.Spam or MessageTone.Suspicious;

    private static bool ContainsAny(string text, string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    // ── Keyword lists (Swedish + English) ────────────────────────────
    // Note: These are intentionally minimal starter sets. Real production
    // systems would use ML classifiers, but rule-based is sufficient for
    // bot-generated safety signals feeding into BotObserver.

    private static readonly string[] _sexualKeywords =
    {
        "naken", "nakenbilder", "nudes", "sex", "knulla", "hora",
        "onlyfans", "sugar daddy", "sugar mommy", "cam show",
        "skicka bilder", "visa dig", "klä av"
    };

    private static readonly string[] _spamKeywords =
    {
        "kolla min länk", "följ mig på", "klicka här",
        "investera", "bitcoin", "crypto", "krypto",
        "gratis pengar", "vinn", "du har vunnit",
        "skicka pengar", "swisha mig", "swish till",
        "www.", "http://", "https://", ".com/", ".se/"
    };

    private static readonly string[] _suspiciousKeywords =
    {
        "ge mig ditt nummer", "vad är din adress", "var bor du exakt",
        "snapchat", "whatsapp", "telegram", "wickr",
        "flytta till", "skriv till mig på", "kontakta mig utanför",
        "skicka ditt personnummer", "ange ditt kort"
    };

    private static readonly string[] _flirtyKeywords =
    {
        "vacker", "snygg", "söt", "charmig", "fin",
        "kram", "puss", "hjärta", "saknar dig",
        "drömmer om", "fantastisk", "underbar",
        "vill träffa dig", "längtar", "❤", "😍", "😘", "💕"
    };

    private static readonly string[] _coldKeywords =
    {
        "nej tack", "inte intresserad", "sluta skriv",
        "lämna mig ifred", "blockera", "farväl",
        "adjö", "hej då", "orkar inte"
    };
}
