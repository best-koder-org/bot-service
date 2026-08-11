namespace BotService.Models;

/// <summary>
/// A/B test experiment comparing different bot conversation strategies.
/// Groups are defined by JSON config, metrics tracked per group.
/// </summary>
public class Experiment
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ExperimentStatus Status { get; set; } = ExperimentStatus.Draft;
    
    /// <summary>JSON config for group A (e.g. opener style, chattiness, response delay)</summary>
    public string GroupAConfig { get; set; } = "{}";
    
    /// <summary>JSON config for group B</summary>
    public string GroupBConfig { get; set; } = "{}";
    
    /// <summary>JSON metrics collected during experiment</summary>
    public string MetricsJson { get; set; } = "{}";
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    
    /// <summary>Number of bots per group</summary>
    public int BotsPerGroup { get; set; } = 5;
    
    /// <summary>Winning group (null if not yet determined)</summary>
    public string? Winner { get; set; }
}

public enum ExperimentStatus
{
    Draft,
    Running,
    Completed,
    Cancelled
}
