using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SwitchAchievements.Core;

namespace SwitchAchievements.Longshot;

/// <summary>
/// <see cref="IProcessProvider"/> backed by System.Diagnostics plus a Toolhelp32
/// snapshot for parent-process IDs. Command lines are intentionally not collected
/// here (see the M3 privacy requirement: opt-in, diagnostic-only).
/// </summary>
public sealed class WindowsProcessProvider : IProcessProvider
{
    public IReadOnlyList<GameProcessInfo> GetProcesses(string processName)
    {
        var parents = BuildParentIdMap();
        var result = new List<GameProcessInfo>();

        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName(processName);
        }
        catch
        {
            return result;
        }

        foreach (var proc in procs)
        {
            try
            {
                var path = GetExecutablePath(proc);
                var startUtc = GetStartUtc(proc);
                var uid = unchecked((uint)proc.Id);
                int? parent = parents.TryGetValue(uid, out var pp) && pp != 0 ? (int?)pp : null;

                result.Add(new GameProcessInfo(proc.Id, path, startUtc)
                {
                    ParentProcessId = parent,
                    CommandLine = null,
                });
            }
            finally
            {
                proc.Dispose();
            }
        }

        return result;
    }

    private static string GetExecutablePath(Process proc)
    {
        try
        {
            return proc.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static DateTime GetStartUtc(Process proc)
    {
        try
        {
            return proc.StartTime.ToUniversalTime();
        }
        catch
        {
            return default;
        }
    }

    private static Dictionary<uint, uint> BuildParentIdMap()
    {
        var map = new Dictionary<uint, uint>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapprocess, 0);
        if (snapshot == new IntPtr(-1))
            return map;

        try
        {
            var entry = new NativeMethods.ProcessEntry32W
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32W>(),
            };

            if (NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    map[entry.th32ProcessID] = entry.th32ParentProcessID;
                }
                while (NativeMethods.Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return map;
    }
}