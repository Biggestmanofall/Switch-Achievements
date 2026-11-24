using SwitchAchievements.App.Infrastructure.Settings;
using SwitchAchievements.App.Services;
using SwitchAchievements.App.ViewModels;

namespace SwitchAchievements.Tests;

public class MainViewModelTests
{
    private static SettingsService NewSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sa-vm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new SettingsService(dir);
    }

    [Fact]
    public void Monitoring_status_text_updates_with_state()
    {
        var vm = new MainViewModel(NewSettings());
        Assert.Equal("Stopped", vm.MonitoringStatusText);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.MonitoringState = MonitoringState.Monitoring;

        Assert.Equal("Monitoring", vm.MonitoringStatusText);
        Assert.Contains(nameof(MainViewModel.MonitoringState), changed);
        Assert.Contains(nameof(MainViewModel.MonitoringStatusText), changed);
    }

    [Fact]
    public void OpenSettingsCommand_raises_requested_event()
    {
        var vm = new MainViewModel(NewSettings());
        var raised = false;
        vm.OpenSettingsRequested += () => raised = true;

        vm.OpenSettingsCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public void Application_status_raises_property_changed()
    {
        var vm = new MainViewModel(NewSettings());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ApplicationStatus = "Ready";

        Assert.Contains(nameof(MainViewModel.ApplicationStatus), changed);
    }
}
