using BotService.Data;
using BotService.Models;
using Microsoft.EntityFrameworkCore;

namespace BotService.Services;

/// <summary>
/// T365 — Self-tuning persona behaviors based on interaction success rates.
/// After min interactions, adjusts swipe probability, chattiness, and message frequency
/// to optimize what works for each persona.
/// </summary>
public class BehaviorLearningService
{
    private readonly BotDbContext _db;
    private readonly BotPersonaEngine _engine;
    private readonly ILogger<BehaviorLearningService> _logger;
    private readonly BotMetrics _metrics;

    private const int MinInteractionsForLearning = 10;
    private const double MinSwipeProb = 0.1;
    private const double MaxSwipeProb = 0.9;
    private const double LearningRate = 0.05;

    public BehaviorLearningService(
        BotDbContext db,
        BotPersonaEngine engine,
        BotMetrics metrics,
        ILogger<BehaviorLearningService> logger)
    {
        _db = db;
        _engine = engine;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Evaluate all active bots and adjust behaviors based on recent performance.
    /// Call periodically (e.g., every 30 min via BotReporter).
    /// </summary>
    public async Task LearnAsync(CancellationToken ct = default)
    {
        var bots = await _db.BotStates
            .Where(b => b.Status == BotStatus.Active)
            .ToListAsync(ct);

        foreach (var bot in bots)
        {
            var totalActions = bot.SwipesToday + bot.MessagesSentToday;
            if (totalActions < MinInteractionsForLearning)
                continue;

            var persona = _engine.GetPersonaById(bot.PersonaId);
            if (persona == null) continue;

            var changed = false;

            // Rule 1: If match rate is high, increase swipe probability
            var matchRate = bot.SwipesToday > 0
                ? (double)bot.MatchCount / bot.SwipesToday
                : 0;

            if (matchRate > 0.1 && persona.Behavior.SwipeRightProbability < MaxSwipeProb)
            {
                persona.Behavior.SwipeRightProbability =
                    Math.Min(MaxSwipeProb, persona.Behavior.SwipeRightProbability + LearningRate);
                changed = true;
                _metrics.Increment("bot_behavior_swipe_prob_up");
                _logger.LogDebug("Bot {Id}: ↑ swipe prob to {P:F2} (matchRate={M:P1})",
                    bot.PersonaId, persona.Behavior.SwipeRightProbability, matchRate);
            }
            else if (bot.SwipesToday >= 20 && matchRate < 0.02 && persona.Behavior.SwipeRightProbability > MinSwipeProb)
            {
                // Rule 2: Many swipes but no matches — lower swipe probability (be more selective)
                persona.Behavior.SwipeRightProbability =
                    Math.Max(MinSwipeProb, persona.Behavior.SwipeRightProbability - LearningRate);
                changed = true;
                _metrics.Increment("bot_behavior_swipe_prob_down");
                _logger.LogDebug("Bot {Id}: ↓ swipe prob to {P:F2} (matchRate={M:P1})",
                    bot.PersonaId, persona.Behavior.SwipeRightProbability, matchRate);
            }

            // Rule 3: If conversation count is high, nudge chattiness up
            if (bot.ConversationCount >= 5 && persona.Behavior.Chattiness == "low")
            {
                persona.Behavior.Chattiness = "medium";
                changed = true;
                _metrics.Increment("bot_behavior_chattiness_up");
                _logger.LogInformation("Bot {Id}: chattiness low → medium ({Conv} conversations)",
                    bot.PersonaId, bot.ConversationCount);
            }
            else if (bot.ConversationCount >= 15 && persona.Behavior.Chattiness == "medium")
            {
                persona.Behavior.Chattiness = "high";
                changed = true;
                _metrics.Increment("bot_behavior_chattiness_up");
                _logger.LogInformation("Bot {Id}: chattiness medium → high ({Conv} conversations)",
                    bot.PersonaId, bot.ConversationCount);
            }

            // Rule 4: Decrease message frequency if many messages but few conversations
            if (bot.MessagesSentToday >= 50 && bot.ConversationCount < 3 && persona.Behavior.MaxDailyMessages > 10)
            {
                persona.Behavior.MaxDailyMessages = Math.Max(5, persona.Behavior.MaxDailyMessages - 5);
                changed = true;
                _metrics.Increment("bot_behavior_msgs_down");
                _logger.LogDebug("Bot {Id}: ↓ max msgs to {M} (low conversation rate)",
                    bot.PersonaId, persona.Behavior.MaxDailyMessages);
            }

            if (changed)
            {
                _metrics.Increment("bot_behavior_adjustments_total");
                await _db.SaveChangesAsync(ct);
            }
        }
    }
}
