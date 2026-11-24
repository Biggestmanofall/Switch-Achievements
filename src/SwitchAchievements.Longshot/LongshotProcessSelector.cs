using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwitchAchievements.Core;

namespace SwitchAchievements.Longshot;

/// <summary>
/// Longshot-specific front end to the generic <see cref="ProcessSelector"/> policy.
/// It classifies which processes are the recompiled game binary, builds
/// per-process diagnostics, and delegates the none/unique/ambiguous decision.
/// </summary>
public static class LongshotProcessSelector
{
    /// <summary>
    /// The recompiled game binary lives at <c>&lt;folder&gt;\bin\longshot.exe</c>;
    /// the launcher stub is <c>&lt;folder&gt;\longshot.exe</c>.
    /// </summary>
    public static bool IsGameBinaryPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        return string.Equals(Path.GetFileName(executablePath), "longshot.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(executablePath)), "bin", StringComparison.OrdinalIgnoreCase);
    }

    public static ProcessSelectionResult Select(
        IReadOnlyList<GameProcessInfo> processes,
        BridgeProbeResult? bridge,
        VerifiedOwnership? verifiedOwnership = null)
    {
        var manifest = new List<string>();

        foreach (var p in processes)
        {
            var parent = p.ParentProcessId?.ToString() ?? "?";
            var start = p.ProcessStartUtc == default ? "?" : p.ProcessStartUtc.ToString("O");
            var path = string.IsNullOrWhiteSpace(p.ExecutablePath) ? "<unavailable>" : p.ExecutablePath;
            var role = IsGameBinaryPath(p.ExecutablePath) ? "game" : "stub/other";
            manifest.Add($"pid={p.ProcessId} parent={parent} startUtc={start} path={path} role={role}");
        }

        if (bridge?.ExperimentalServerProcessId is not null)
        {
            manifest.Add(
                $"experimental pipe server pid={bridge.ExperimentalServerProcessId} (diagnostic only; not used for selection)");
        }

        var candidates = processes.Where(p => IsGameBinaryPath(p.ExecutablePath)).ToList();
        var result = ProcessSelector.Select(candidates, bridge, verifiedOwnership);

        manifest.AddRange(result.Diagnostics);
        return result with { Diagnostics = manifest };
    }
}