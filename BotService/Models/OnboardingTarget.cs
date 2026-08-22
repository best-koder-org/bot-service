using System.ComponentModel.DataAnnotations;

namespace BotService.Models;

/// <summary>
/// Tracks a human tester that the bot swarm has already onboarded (pre-liked),
/// so onboarding assist never double-likes the same person.
/// </summary>
public class OnboardingTarget
{
    public int Id { get; set; }

    [Required]
    public string KeycloakUserId { get; set; } = string.Empty;

    public int ProfileId { get; set; }

    public DateTime AssistedAt { get; set; } = DateTime.UtcNow;
}
