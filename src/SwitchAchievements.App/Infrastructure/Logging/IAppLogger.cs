namespace SwitchAchievements.App.Infrastructure.Logging;

/// <summary>
/// Minimal logging abstraction used by the desktop application. Kept free of any
/// specific logging framework so the app remains lightweight and testable.
/// </summary>
public interface IAppLogger : IDisposable
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

