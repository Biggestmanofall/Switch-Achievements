using System.IO;
using System.Text;

namespace SwitchAchievements.App.Infrastructure.Logging;

/// <summary>
/// Thread-safe file logger. Each run opens a new timestamped log file and writes
/// lines using append-per-write so a crash cannot leave a truncated file.
/// Logging is strictly best-effort: any failure to create the directory or write
/// a line is swallowed so logging can never crash the application.
/// </summary>
public sealed class FileLogger : IAppLogger, IDisposable
{
    private readonly object _lock = new();
    private readonly string _directory;
    private readonly string _prefix;
    private readonly TimeSpan _retention;
    private string? _path;

    public FileLogger(string directory, string prefix, TimeSpan? retention = null)
    {
        _directory = directory;
        _prefix = prefix;
        _retention = retention ?? TimeSpan.FromDays(7);

        try
        {
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _path = Path.Combine(directory, $"{prefix}-{stamp}.log");
            DeleteExpiredLogs();
            Info($"log started: {_path}");
        }
        catch (IOException)
        {
            _path = null; // logging unavailable; calls become no-ops
        }
        catch (UnauthorizedAccessException)
        {
            _path = null;
        }
    }

    public string? LogPath => _path;

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message) => Write("ERROR", message);

    public void Dispose()
    {
        // Nothing held open; AppendAllText opens and closes the file per write.
    }

    private void Write(string level, string message)
    {
        if (_path is null)
            return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Best-effort logging; never let a write failure crash the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void DeleteExpiredLogs()
    {
        try
        {
            var cutoff = DateTime.Now - _retention;
            foreach (var file in Directory.GetFiles(_directory, $"{_prefix}-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
