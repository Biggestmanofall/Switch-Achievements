using System;
using System.IO;
using System.Text;

namespace SwitchAchievements.Longshot.Poc;

/// <summary>
/// Simple thread-safe file logger. Only process/build/bridge information is written —
/// no game files, no save data and no personal information.
/// </summary>
public sealed class DiagnosticsLogger : IDisposable
{
    private readonly object _lock = new();
    private readonly string _path;

    public DiagnosticsLogger(string directory, string prefix)
    {
        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _path = System.IO.Path.Combine(directory, $"{prefix}-{stamp}.log");
        Info($"log started: {_path}");
    }

    public string LogPath => _path;

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        lock (_lock)
        {
            File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    public void Dispose()
    {
        // Nothing held open (AppendAllText opens/closes per write).
    }
}