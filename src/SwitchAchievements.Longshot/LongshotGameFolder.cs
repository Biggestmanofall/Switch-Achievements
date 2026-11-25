using System.IO;

namespace SwitchAchievements.Longshot;

/// <summary>
/// Resolves a Longshot game's installation folder from the game process's executable
/// path (<c>&lt;folder&gt;\bin\longshot.exe</c>). Shared by identity collection (M5.6.4)
/// and live-tracking setup (M5.8.2), so both validate the path the same way. Failure
/// reasons are fixed texts, never paths.
/// </summary>
internal static class LongshotGameFolder
{
    /// <summary>The game image Longshot itself looks for beside <c>bin\</c> to find its folder.</summary>
    internal const string ImageFileName = "image.bin";

    /// <summary>
    /// Validates the executable path and derives the game folder above <c>bin\</c>. Rejects
    /// empty, device-namespace, UNC, relative, invalid and non-normalized paths, executables
    /// that are not <c>...\bin\longshot.exe</c>, and folders on network drives.
    /// </summary>
    internal static bool TryGetGameDirectory(
        string? executablePath,
        Func<string, bool> isOnNetworkDrive,
        out string gameDirectory,
        out string failure)
    {
        gameDirectory = string.Empty;

        if (string.IsNullOrWhiteSpace(executablePath))
            return Fail("the selected executable path is empty", out failure);

        if (executablePath.StartsWith(@"\\?\", StringComparison.Ordinal) || executablePath.StartsWith(@"\\.\", StringComparison.Ordinal)
            || executablePath.StartsWith("//?/", StringComparison.Ordinal) || executablePath.StartsWith("//./", StringComparison.Ordinal))
            return Fail("device-namespace executable paths are not supported", out failure);

        if (executablePath.StartsWith(@"\\", StringComparison.Ordinal) || executablePath.StartsWith("//", StringComparison.Ordinal))
            return Fail("network (UNC) executable paths are not supported", out failure);

        if (!Path.IsPathFullyQualified(executablePath))
            return Fail("the selected executable path is not fully qualified", out failure);

        string normalized;
        try
        {
            normalized = Path.GetFullPath(executablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail("the selected executable path is invalid", out failure);
        }

        if (!string.Equals(normalized, executablePath, StringComparison.Ordinal))
            return Fail("the selected executable path is not normalized", out failure);

        if (!LongshotProcessSelector.IsGameBinaryPath(executablePath))
            return Fail(@"the selected executable is not a Longshot game binary (expected ...\bin\longshot.exe)", out failure);

        var binDirectory = Path.GetDirectoryName(executablePath);
        var directory = binDirectory is null ? null : Path.GetDirectoryName(binDirectory);
        if (string.IsNullOrEmpty(directory))
            return Fail("the selected executable has no game folder above bin", out failure);

        if (isOnNetworkDrive(directory))
            return Fail("network-drive installations are not supported", out failure);

        gameDirectory = directory;
        failure = string.Empty;
        return true;
    }

    /// <summary>
    /// <see cref="TryGetGameDirectory"/>, plus Longshot's own rule for its game folder: the
    /// folder above <c>bin\</c> holds <see cref="ImageFileName"/>. Used before writing there.
    /// </summary>
    internal static bool TryResolveInstallation(
        string? executablePath,
        Func<string, bool> isOnNetworkDrive,
        Func<string, bool> fileExists,
        out string gameDirectory,
        out string failure)
    {
        if (!TryGetGameDirectory(executablePath, isOnNetworkDrive, out gameDirectory, out failure))
            return false;

        if (!fileExists(Path.Join(gameDirectory, ImageFileName)))
        {
            gameDirectory = string.Empty;
            return Fail("the game folder has no image.bin", out failure);
        }

        return true;
    }

    private static bool Fail(string reason, out string failure)
    {
        failure = reason;
        return false;
    }
}
