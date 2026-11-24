using System;
using System.IO;
using System.Text.Json;

namespace SwitchAchievements.Longshot.Poc;

public sealed class BuildIdentity
{
    public string? Version { get; set; }
    public string? Source { get; set; }
    public string? Builder { get; set; }
    public string? NupBuildId { get; set; }
    public string? GameProfileName { get; set; }
}

public static class BuildIdentifier
{
    /// <summary>
    /// Read build identity from a Longshot game folder: longshot_version.txt
    /// (version/source/builder) and the install-root recomplauncher.json (nup_build_id).
    /// </summary>
    public static BuildIdentity? FromGameFolder(string gameFolder)
    {
        var result = new BuildIdentity();
        bool found = false;

        var versionFile = Path.Combine(gameFolder, "longshot_version.txt");
        if (File.Exists(versionFile))
        {
            foreach (var raw in File.ReadLines(versionFile))
            {
                var eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                var key = raw[..eq].Trim();
                var value = raw[(eq + 1)..].Trim();
                switch (key)
                {
                    case "version": result.Version = value; found = true; break;
                    case "source": result.Source = value; found = true; break;
                    case "builder": result.Builder = value; found = true; break;
                }
            }
        }

        var installRoot = Path.GetDirectoryName(gameFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (installRoot is not null)
        {
            var rl = Path.Combine(installRoot, "recomplauncher.json");
            if (File.Exists(rl))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(rl));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("name", out var n)) result.GameProfileName = n.GetString();
                    if (root.TryGetProperty("game", out var g) &&
                        g.TryGetProperty("check", out var c) &&
                        c.TryGetProperty("nup_build_id", out var nup))
                    {
                        result.NupBuildId = nup.GetString();
                        found = found || result.NupBuildId is not null;
                    }
                }
                catch
                {
                    // non-fatal: recomplauncher.json missing or malformed
                }
            }
        }

        return found ? result : null;
    }
}