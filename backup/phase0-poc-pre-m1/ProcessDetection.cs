using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SwitchAchievements.Longshot.Poc;

public sealed record GameProcess(int Pid, string ExecutablePath, string GameFolder, bool IsGameBinary);

public static class ProcessDetection
{
    /// <summary>
    /// Find the Longshot launcher stub and the real recompiled game. Both are named
    /// "longshot.exe"; the game lives at &lt;folder&gt;\bin\longshot.exe.
    /// </summary>
    public static IReadOnlyList<GameProcess> FindLongshotProcesses()
    {
        var list = new List<GameProcess>();
        foreach (var proc in Process.GetProcessesByName("longshot"))
        {
            try
            {
                var exe = proc.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exe)) continue;

                var binDir = Path.GetDirectoryName(exe);
                if (binDir is null) continue;

                bool isGame = string.Equals(Path.GetFileName(binDir), "bin", StringComparison.OrdinalIgnoreCase);
                string gameFolder = isGame ? (Path.GetDirectoryName(binDir) ?? string.Empty) : string.Empty;

                list.Add(new GameProcess(proc.Id, exe, gameFolder, isGame));
            }
            catch
            {
                // MainModule can throw for elevated/protected processes; skip those.
            }
            finally
            {
                proc.Dispose();
            }
        }
        return list;
    }
}