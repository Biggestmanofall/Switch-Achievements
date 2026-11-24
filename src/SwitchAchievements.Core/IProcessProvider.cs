namespace SwitchAchievements.Core;

/// <summary>
/// An operating-system process, as observed by the detection layer. This is the
/// minimal shape needed to identify a game without reaching into integration details.
/// </summary>
public sealed record GameProcessInfo(int ProcessId, string ExecutablePath, DateTime ProcessStartUtc)
{
    /// <summary>Parent process id, when available (used for the two-process investigation).</summary>
    public int? ParentProcessId { get; init; }

    /// <summary>Raw command line, when available (used for the two-process investigation).</summary>
    public string? CommandLine { get; init; }
}

/// <summary>
/// Abstraction over OS process enumeration. It exists so detection/selection
/// logic can be unit-tested with fake process lists (for example the "two
/// identical processes" scenario) without needing the real game running.
/// </summary>
public interface IProcessProvider
{
    IReadOnlyList<GameProcessInfo> GetProcesses(string processName);
}