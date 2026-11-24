using SwitchAchievements.App.Infrastructure.Settings;

namespace SwitchAchievements.Tests;

public class SettingsServiceTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sa-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Missing_file_creates_default_settings()
    {
        var dir = NewTempDir();
        var service = new SettingsService(dir);
        Assert.True(service.Current.ShowWindowOnLaunch);
        Assert.True(service.Current.MinimizeToTrayOnClose);
        Assert.True(File.Exists(Path.Combine(dir, "config.json")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Save_and_load_round_trips_values()
    {
        var dir = NewTempDir();
        var service = new SettingsService(dir);
        service.Save(new AppSettings { ShowWindowOnLaunch = false, MinimizeToTrayOnClose = false });

        var reloaded = new SettingsService(dir);
        Assert.False(reloaded.Current.ShowWindowOnLaunch);
        Assert.False(reloaded.Current.MinimizeToTrayOnClose);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Corrupt_json_falls_back_to_defaults_and_backs_up_file()
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "config.json"), "{ this is not valid json !!!");

        var service = new SettingsService(dir);

        Assert.True(service.Current.ShowWindowOnLaunch);
        Assert.True(service.Current.MinimizeToTrayOnClose);

        var backups = Directory.GetFiles(dir, "config.json.corrupt-*");
        Assert.NotEmpty(backups);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Save_writes_valid_json_file()
    {
        var dir = NewTempDir();
        var service = new SettingsService(dir);
        service.Save(new AppSettings { ShowWindowOnLaunch = false, MinimizeToTrayOnClose = true });

        var text = File.ReadAllText(Path.Combine(dir, "config.json"));
        Assert.Contains("\"ShowWindowOnLaunch\": false", text);
        Directory.Delete(dir, true);
    }
}
