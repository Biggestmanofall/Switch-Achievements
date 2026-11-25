using SwitchAchievements.App.Infrastructure.Settings;
using SwitchAchievements.App.Infrastructure.Startup;
using SwitchAchievements.App.ViewModels;

namespace SwitchAchievements.Tests.Startup;

/// <summary>M5.7 Task 4: the StartWithWindows setting, its persistence, and the Settings window flow.</summary>
public sealed class StartWithWindowsSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-start-with-windows-tests", Guid.NewGuid().ToString("N"));

    public StartWithWindowsSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private sealed class FakeRegistration : IStartupRegistration
    {
        public List<bool> Applied { get; } = new();
        public Exception? Failure { get; set; }

        public bool IsRegistered() => Applied.Count > 0 && Applied[^1];

        public void Apply(bool enabled)
        {
            Applied.Add(enabled);
            if (Failure is not null)
                throw Failure;
        }
    }

    // ---------------- model and persistence ----------------

    [Fact]
    public void Defaults_are_unchanged_and_start_with_windows_is_off()
    {
        var defaults = new AppSettings();
        Assert.False(defaults.StartWithWindows);
        Assert.True(defaults.ShowWindowOnLaunch);
        Assert.True(defaults.MinimizeToTrayOnClose);

        var service = new SettingsService(_dir);
        Assert.False(service.Current.StartWithWindows);
        Assert.True(service.Current.ShowWindowOnLaunch);
        Assert.True(service.Current.MinimizeToTrayOnClose);
    }

    [Fact]
    public void Save_and_reload_preserve_start_with_windows()
    {
        var service = new SettingsService(_dir);
        service.Save(new AppSettings { StartWithWindows = true, ShowWindowOnLaunch = false, MinimizeToTrayOnClose = true });

        Assert.True(service.Current.StartWithWindows);
        var reloaded = new SettingsService(_dir);
        Assert.True(reloaded.Current.StartWithWindows);
        Assert.False(reloaded.Current.ShowWindowOnLaunch);
        Assert.True(reloaded.Current.MinimizeToTrayOnClose);
    }

    [Fact]
    public void An_older_config_without_the_key_loads_as_false()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"),
            "{\n  \"ShowWindowOnLaunch\": false,\n  \"MinimizeToTrayOnClose\": false\n}");

        var service = new SettingsService(_dir);

        Assert.False(service.Current.StartWithWindows);
        Assert.False(service.Current.ShowWindowOnLaunch);
        Assert.False(service.Current.MinimizeToTrayOnClose);
    }

    // ---------------- settings window flow ----------------

    [Fact]
    public void The_checkbox_starts_from_the_saved_setting()
    {
        var service = new SettingsService(_dir);
        Assert.False(new SettingsViewModel(service).StartWithWindows);

        service.Save(new AppSettings { StartWithWindows = true });
        Assert.True(new SettingsViewModel(service).StartWithWindows);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Saving_persists_and_applies_the_requested_setting(bool enabled)
    {
        var service = new SettingsService(_dir);
        service.Save(new AppSettings { StartWithWindows = !enabled });
        var registration = new FakeRegistration();
        var vm = new SettingsViewModel(service, registration) { StartWithWindows = enabled };
        var closed = false;
        vm.CloseRequested += () => closed = true;

        vm.SaveCommand.Execute(null);

        Assert.Equal(new[] { enabled }, registration.Applied);
        Assert.Equal(enabled, service.Current.StartWithWindows); // live
        Assert.Equal(enabled, new SettingsService(_dir).Current.StartWithWindows); // persisted
        Assert.True(closed);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public void Saving_preserves_the_other_settings()
    {
        var service = new SettingsService(_dir);
        service.Save(new AppSettings { ShowWindowOnLaunch = false, MinimizeToTrayOnClose = false });
        var vm = new SettingsViewModel(service, new FakeRegistration()) { StartWithWindows = true };

        vm.SaveCommand.Execute(null);

        Assert.False(service.Current.ShowWindowOnLaunch);
        Assert.False(service.Current.MinimizeToTrayOnClose);
        Assert.True(service.Current.StartWithWindows);
    }

    [Fact]
    public void Registration_failure_shows_an_error_and_keeps_the_window_open()
    {
        var service = new SettingsService(_dir);
        var registration = new FakeRegistration { Failure = new UnauthorizedAccessException("denied") };
        var vm = new SettingsViewModel(service, registration) { StartWithWindows = true };
        var closed = false;
        vm.CloseRequested += () => closed = true;

        vm.SaveCommand.Execute(null);

        Assert.False(closed);
        Assert.Equal(SettingsViewModel.StartupRegistrationFailedMessage, vm.ErrorMessage);
        Assert.True(service.Current.StartWithWindows); // saved; startup retries the registration

        // A later successful save clears the error and closes.
        registration.Failure = null;
        vm.SaveCommand.Execute(null);
        Assert.True(closed);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public void Cancel_does_not_apply_the_registration()
    {
        var service = new SettingsService(_dir);
        var registration = new FakeRegistration();
        var vm = new SettingsViewModel(service, registration) { StartWithWindows = true };

        vm.CancelCommand.Execute(null);

        Assert.Empty(registration.Applied);
        Assert.False(service.Current.StartWithWindows);
    }
}
