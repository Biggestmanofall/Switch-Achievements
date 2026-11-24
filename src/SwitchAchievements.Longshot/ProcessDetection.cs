using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwitchAchievements.Core;

namespace SwitchAchievements.Longshot;

/// <summary>
/// A Longshot-related process (launcher stub or recompiled game) with the fields
/// the PoC CLI needs for listing and build identity. Selection policy lives in
/// <see cref="LongshotProcessSelector"/>; this type only enumerates and maps.
/// </summary>
public sealed record GameProcess(int Pid, string ExecutablePath, string GameFolder, bool IsGameBinary);

public static class ProcessDetection
{
    /// <summary>
    /// Enumerate Longshot processes (stub and game) via <see cref="WindowsProcessProvider"/>.
    /// Fuller identity (parent PID, start time) is available from that provider.
    /// </summary>
    public static IReadOnlyList<GameProcess> FindLongshotProcesses()
    {
        var infos = new WindowsProcessProvider().GetProcesses("longshot");
        return infos
            .Where(p => !string.IsNullOrWhiteSpace(p.ExecutablePath))
            .Select(ToGameProcess)
            .ToList();
    }

    /// <summary>Map a full process identity to the legacy game-process shape.</summary>
    public static GameProcess ToGameProcess(GameProcessInfo process)
    {
        bool isGame = LongshotProcessSelector.IsGameBinaryPath(process.ExecutablePath);
        string folder = isGame
            ? Path.GetDirectoryName(Path.GetDirectoryName(process.ExecutablePath)) ?? string.Empty
            : string.Empty;
        return new GameProcess(process.ProcessId, process.ExecutablePath, folder, isGame);
    }
}