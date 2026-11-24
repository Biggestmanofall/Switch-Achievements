using SwitchAchievements.App.Infrastructure.Logging;

namespace SwitchAchievements.Tests;

public class FileLoggerTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sa-logs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Writes_info_warn_and_error_lines()
    {
        var dir = NewTempDir();
        var logger = new FileLogger(dir, "app");
        logger.Info("hello");
        logger.Warn("caution");
        logger.Error("bad");

        var text = File.ReadAllText(logger.LogPath!);
        Assert.Contains("INFO ", text);
        Assert.Contains("WARN ", text);
        Assert.Contains("ERROR", text);
        Assert.Contains("hello", text);
        Assert.Contains("caution", text);
        Assert.Contains("bad", text);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Concurrent_writes_do_not_throw()
    {
        var dir = NewTempDir();
        var logger = new FileLogger(dir, "app");

        Parallel.For(0, 100, i => logger.Info($"msg {i}"));

        Assert.True(File.Exists(logger.LogPath));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Rotation_deletes_old_log_files()
    {
        var dir = NewTempDir();
        var oldFile = Path.Combine(dir, "app-20200101-000000.log");
        File.WriteAllText(oldFile, "old");
        File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-30));

        _ = new FileLogger(dir, "app", TimeSpan.FromDays(7));

        Assert.False(File.Exists(oldFile));
        Directory.Delete(dir, true);
    }
}
