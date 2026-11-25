using SwitchAchievements.App;
using SwitchAchievements.App.Infrastructure.Settings;
using SwitchAchievements.App.Services;
using SwitchAchievements.App.ViewModels;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.AppLifecycle;

/// <summary>
/// M5.7 Task 1.1: the "Minimize to tray on close" setting, from the Settings window's
/// Save through the active settings to the main window's close decision.
/// </summary>
public sealed class CloseToTraySettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-close-to-tray-tests", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public CloseToTraySettingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static void SaveViaSettingsWindow(SettingsService service, bool minimizeToTray, bool? showOnLaunch = null)
    {
        var vm = new SettingsViewModel(service) { MinimizeToTrayOnClose = minimizeToTray };
        if (showOnLaunch is { } show)
            vm.ShowWindowOnLaunch = show;
        vm.SaveCommand.Execute(null);
    }

    /// <summary>The decision MainWindow makes when it is closed, read from its view model as it does.</summary>
    private static MainWindowCloseAction CloseDecision(MainViewModel window, bool exitHandlerAttached = true) =>
        MainWindowClosePolicy.Decide(allowClose: false, window.Settings.Current.MinimizeToTrayOnClose, exitHandlerAttached);

    [Fact]
    public void Disabling_persists_the_change()
    {
        var service = new SettingsService(_dir);

        SaveViaSettingsWindow(service, minimizeToTray: false);

        Assert.Contains("\"MinimizeToTrayOnClose\": false", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Disabling_updates_the_active_settings_immediately()
    {
        var service = new SettingsService(_dir);
        var before = service.Current;

        SaveViaSettingsWindow(service, minimizeToTray: false);

        Assert.False(service.Current.MinimizeToTrayOnClose);   // the regression: previously still true
        Assert.True(before.MinimizeToTrayOnClose);              // earlier snapshots are not mutated
    }

    [Fact]
    public void The_setting_survives_a_reload()
    {
        SaveViaSettingsWindow(new SettingsService(_dir), minimizeToTray: false);
        Assert.False(new SettingsService(_dir).Current.MinimizeToTrayOnClose);

        SaveViaSettingsWindow(new SettingsService(_dir), minimizeToTray: true);
        Assert.True(new SettingsService(_dir).Current.MinimizeToTrayOnClose);
    }

    [Fact]
    public void Active_settings_are_a_private_copy_of_what_was_saved()
    {
        var service = new SettingsService(_dir);
        var saved = new AppSettings { MinimizeToTrayOnClose = false };

        service.Save(saved);
        saved.MinimizeToTrayOnClose = true;

        Assert.False(service.Current.MinimizeToTrayOnClose);
    }

    [Fact]
    public void Enabled_setting_hides_the_window_to_the_tray()
    {
        var service = new SettingsService(_dir);
        var main = new MainViewModel(service);
        SaveViaSettingsWindow(service, minimizeToTray: false);

        SaveViaSettingsWindow(service, minimizeToTray: true); // re-enabled in the same session

        Assert.Equal(MainWindowCloseAction.HideToTray, CloseDecision(main));
    }

    [Fact]
    public void Disabled_setting_routes_closing_through_the_coordinated_exit_in_the_same_session()
    {
        var service = new SettingsService(_dir);
        var main = new MainViewModel(service); // created at startup, before the change
        Assert.Equal(MainWindowCloseAction.HideToTray, CloseDecision(main));

        SaveViaSettingsWindow(service, minimizeToTray: false);

        Assert.Equal(MainWindowCloseAction.RequestExit, CloseDecision(main));
        Assert.Equal(MainWindowCloseAction.ShutdownDirectly, CloseDecision(main, exitHandlerAttached: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_allowed_close_always_completes(bool minimizeToTray) =>
        Assert.Equal(MainWindowCloseAction.Close, MainWindowClosePolicy.Decide(allowClose: true, minimizeToTray, exitHandlerAttached: true));

    [Fact]
    public async Task Coordinated_exit_after_disabling_stops_monitoring_and_shuts_down()
    {
        var service = new SettingsService(_dir);
        var main = new MainViewModel(service);
        var logger = new CapturingLogger();
        var created = new List<FakeMonitoringService>();
        var host = new MonitoringHost(logger,
            serviceFactory: catalog => { var s = new FakeMonitoringService(catalog, new RunningCounter()); created.Add(s); return s; },
            catalogLoader: () => IdentityTestData.OotCatalog());
        await host.StartAsync();
        var coordinator = new ShutdownCoordinator(host, logger);
        var shutDown = false;

        SaveViaSettingsWindow(service, minimizeToTray: false);
        Assert.Equal(MainWindowCloseAction.RequestExit, CloseDecision(main));
        await coordinator.RequestExitAsync(() => { }, () => shutDown = true).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(shutDown);
        Assert.True(created[0].IsStopped);
        Assert.Null(host.Service);
    }

    [Fact]
    public void Changing_the_setting_preserves_other_settings()
    {
        var service = new SettingsService(_dir);
        SaveViaSettingsWindow(service, minimizeToTray: true, showOnLaunch: false);

        SaveViaSettingsWindow(service, minimizeToTray: false);

        Assert.False(service.Current.ShowWindowOnLaunch);
        Assert.False(new SettingsService(_dir).Current.ShowWindowOnLaunch);
    }

    [Fact]
    public void Existing_settings_files_remain_compatible()
    {
        // A file written by earlier versions (indented, both keys), plus an unknown key.
        File.WriteAllText(ConfigPath, "{\n  \"ShowWindowOnLaunch\": true,\n  \"MinimizeToTrayOnClose\": false,\n  \"FutureSetting\": 3\n}");

        var service = new SettingsService(_dir);
        Assert.True(service.Current.ShowWindowOnLaunch);
        Assert.False(service.Current.MinimizeToTrayOnClose);
        Assert.Empty(Directory.GetFiles(_dir, "config.json.corrupt-*"));

        // A file missing the key keeps the default.
        File.WriteAllText(ConfigPath, "{ \"ShowWindowOnLaunch\": false }");
        Assert.True(new SettingsService(_dir).Current.MinimizeToTrayOnClose);

        // Saving still writes the same format and key names.
        SaveViaSettingsWindow(service, minimizeToTray: true);
        var json = File.ReadAllText(ConfigPath);
        Assert.Contains("\"ShowWindowOnLaunch\": true", json);
        Assert.Contains("\"MinimizeToTrayOnClose\": true", json);
    }
}
