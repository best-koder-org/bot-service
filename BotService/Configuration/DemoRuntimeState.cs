namespace BotService.Configuration;

/// <summary>
/// Runtime-toggleable demo mode flags. Hosted services read this (falling back to
/// appsettings values) so the dashboard / DemoController can turn demo mode on/off
/// live without a restart.
/// </summary>
public class DemoRuntimeState
{
    public bool Enabled { get; set; }

    public bool ReactiveOnly { get; set; } = true;
}
