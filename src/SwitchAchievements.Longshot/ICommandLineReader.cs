using SwitchAchievements.Core;

namespace SwitchAchievements.Longshot;

/// <summary>
/// Reads another process's command line, solely so that
/// <see cref="LongshotProcessRoleClassifier"/> can recognise Longshot's
/// crash-reporter helpers.
///
/// Privacy contract: callers must parse only the arguments they need and
/// discard the returned string immediately. A command line can contain user
/// paths; it must never be stored, logged, put in diagnostics or exception
/// messages, or copied into <see cref="GameProcessInfo.CommandLine"/>.
/// </summary>
public interface ICommandLineReader
{
    /// <summary>
    /// Returns the command line of exactly the process instance described by
    /// <paramref name="process"/> (process id and start time), or null when it
    /// cannot be read reliably (access denied, the process exited, the id now
    /// belongs to another process, or the query failed). Implementations should
    /// return null rather than throw; callers still treat a throw as "unreadable".
    /// </summary>
    string? ReadCommandLine(GameProcessInfo process);
}
