using System.Text.Json;

namespace BotService.Services.Content;

/// <summary>
/// Provides canned Swedish dating messages organized by conversation stage.
/// Loaded from Content/messages.json. Expandable — just add more messages to the JSON.
/// </summary>
public class MessageContentProvider
{
    private readonly ILogger<MessageContentProvider> _logger;
    private readonly Random _random = new();
    private Dictionary<string, List<string>> _messages = new();

    public MessageContentProvider(ILogger<MessageContentProvider> logger)
    {
        _logger = logger;
    }

    public void LoadMessages(string contentDirectory)
    {
        var path = Path.Combine(contentDirectory, "messages.json");
        if (!File.Exists(path))
        {
            _logger.LogWarning("Messages file not found: {Path}, using defaults", path);
            _messages = GetDefaultMessages();
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            _messages = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? GetDefaultMessages();
            
            var total = _messages.Values.Sum(v => v.Count);
            _logger.LogInformation("Loaded {Total} messages across {Categories} categories", total, _messages.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load messages, using defaults");
            _messages = GetDefaultMessages();
        }
    }

    /// <summary>Get a random message for a conversation stage</summary>
    public string GetMessage(string stage = "opener")
    {
        if (_messages.TryGetValue(stage, out var pool) && pool.Count > 0)
            return pool[_random.Next(pool.Count)];
        
        // Fallback to opener if stage not found
        if (_messages.TryGetValue("opener", out var openers) && openers.Count > 0)
            return openers[_random.Next(openers.Count)];
        
        return "Hej! 👋";
    }

    /// <summary>Get a message appropriate for the conversation depth</summary>
    public string GetMessageForDepth(int messageCount)
    {
        return messageCount switch
        {
            0 => GetMessage("opener"),
            1 or 2 => GetMessage("followup"),
            3 or 4 => GetMessage("deepening"),
            _ => GetMessage("continuing")
        };
    }

    private static Dictionary<string, List<string>> GetDefaultMessages() => new()
    {
        ["opener"] = new()
        {
            "Hej! Hur mår du? 😊",
            "Tjena! Såg att du också gillar vandring 🏔️",
            "Hej hej! Kul profil, vad gör du en sån här dag?",
            "Hej! Vad har du för planer i helgen? 🌟",
            "Tja! Din profil fångade mitt öga direkt ✨",
            "Hallå! Gillar din stil 😄",
            "Hej! Verkar som vi har liknande intressen!",
            "God morgon/kväll! Hur är läget? ☀️",
            "Hej där! Berätta lite om dig själv 🙂",
            "Tjena! Är du från Stockholm också?"
        },
        ["followup"] = new()
        {
            "Haha ja verkligen! Var brukar du hänga?",
            "Åh nice! Berättar du mer? 😊",
            "Spännande! Hur länge har du hållit på med det?",
            "Coolt! Jag har alltid velat testa det",
            "Jag förstår precis vad du menar! 😄",
            "Det låter riktigt kul faktiskt!",
            "Oj, det visste jag inte! Berätta mer",
            "Haha det var roligt! 😂",
            "Samma här! Vi verkar ha mycket gemensamt",
            "Intressant! Vad tycker du om Stockholm?"
        },
        ["deepening"] = new()
        {
            "Var brukar du vandra egentligen? Jag gillar Sörmlandsleden",
            "Har du testat restaurangen Fotografiska? Riktigt bra mat!",
            "Vilken typ av musik lyssnar du på? 🎵",
            "Vad drömmer du om att göra just nu?",
            "Om du kunde resa var som helst, vart skulle du åka? ✈️",
            "Vad jobbar du med? Verkar intressant!",
            "Favorit filmgenre? Jag är svag för thrillers 🎬",
            "Gillar du att laga mat? Jag försöker lära mig! 🍳"
        },
        ["continuing"] = new()
        {
            "Haha okej det var en bra poäng! 😄",
            "Mmm absolut, håller med helt!",
            "Ja or hur! Det tänkte jag inte ens på",
            "Vi verkar tänka ganska lika 🙂",
            "Det var ju gött att höra!",
            "Lite random men... gillar du kaffe eller te? ☕",
            "Hoppas du haft en bra dag idag!",
            "Vad gör du ikväll? 🌙"
        },
        ["fika_invite"] = new()
        {
            "Ska vi ses på en fika nån gång? ☕",
            "Vi borde träffas IRL! Har du tid nån dag?",
            "Det vore kul att ta en promenad och snacka mer 🚶",
            "Vill du hänga nån dag den här veckan?",
            "Kaffe på Söder nån gång? 😊"
        }
    };
}
