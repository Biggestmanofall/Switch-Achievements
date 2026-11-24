using SwitchAchievements.Core;

namespace SwitchAchievements.Tests.Fakes;

/// <summary>Scriptable <see cref="IProcessProvider"/> for monitoring tests.</summary>
public sealed class FakeProcessProvider : IProcessProvider
{
    public IReadOnlyList<GameProcessInfo> Processes { get; set; } = Array.Empty<GameProcessInfo>();

    public Func<string, IReadOnlyList<GameProcessInfo>>? OnGetProcesses { get; set; }

    public IReadOnlyList<GameProcessInfo> GetProcesses(string processName)
        => OnGetProcesses is not null ? OnGetProcesses(processName) : Processes;
}