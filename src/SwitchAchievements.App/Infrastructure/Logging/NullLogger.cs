namespace SwitchAchievements.App.Infrastructure.Logging;

/// <summary>
/// Logging no-op used when logging is unavailable or disabled.
/// </summary>
public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();

    private NullLogger() { }

    public void Info(string message) { }

    public void Warn(string message) { }

    public void Error(string message) { }

    public void Dispose() { }
}
