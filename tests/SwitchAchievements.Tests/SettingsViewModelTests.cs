using SwitchAchievements.App.Infrastructure.Settings;
using SwitchAchievements.App.ViewModels;

namespace SwitchAchievements.Tests;

public class SettingsViewModelTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sa-settings-vm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Save_persists_values()
    {
        var dir = NewTempDir();
        var service = new SettingsService(dir);

        var vm = new SettingsViewModel(service);
        vm.ShowWindowOnLaunch = false;
        vm.MinimizeToTrayOnClose = false;

        var closed = false;
        vm.CloseRequested += () => closed = true;
        vm.SaveCommand.Execute(null);

        Assert.True(closed);

        var reloaded = new SettingsService(dir);
        Assert.False(reloaded.Current.ShowWindowOnLaunch);
        Assert.False(reloaded.Current.MinimizeToTrayOnClose);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Cancel_closes_without_saving()
    {
        var dir = NewTempDir();
        var service = new SettingsService(dir);

        var vm = new SettingsViewModel(service);
        vm.ShowWindowOnLaunch = false;

        var closed = false;
        vm.CloseRequested += () => closed = true;
        vm.CancelCommand.Execute(null);

        Assert.True(closed);

        var reloaded = new SettingsService(dir);
        Assert.True(reloaded.Current.ShowWindowOnLaunch); // unchanged
        Directory.Delete(dir, true);
    }
}
