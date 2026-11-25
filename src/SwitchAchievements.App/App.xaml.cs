using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.App.Infrastructure.Settings;
using SwitchAchievements.App.Infrastructure.SingleInstance;
using SwitchAchievements.App.Infrastructure.Startup;
using SwitchAchievements.App.Services;
using SwitchAchievements.App.ViewModels;

namespace SwitchAchievements.App;

/// <summary>
/// Application composition root. Builds and owns every service, and coordinates
/// startup, single-instance enforcement, tray lifecycle, and shutdown.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\SwitchAchievements.SingleInstance";
    private const string ActivationEventName = @"Local\SwitchAchievements.Activation";

    private IAppLogger? _logger;
    private SettingsService? _settings;
    private SingleInstanceGuard? _guard;
    private ActivationListener? _activation;
    private TrayIconController? _tray;
    private MainWindow? _mainWindow;
    private MonitoringHost? _monitoring;
    private ShutdownCoordinator? _shutdown;
    private DashboardViewModel? _dashboard;
    private IStartupRegistration? _startupRegistration;
    private MonitoringRestartCoordinator? _restart;
    private NotificationMonitor? _notifications;
    private LiveTrackingSetupCoordinator? _liveTrackingSetup;
    private Longshot.LongshotGameIntegration? _setupIntegration;
    private bool _trayAttached;
    private bool _isShuttingDown;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 1. Single-instance guard. A second launch signals the primary and exits.
        _guard = new SingleInstanceGuard(MutexName);
        if (!_guard.IsFirstInstance)
        {
            ActivationSignal.TrySignal(ActivationEventName);
            _guard.Dispose();
            _guard = null;
            Shutdown();
            return;
        }

        // 2. Logging.
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwitchAchievements",
            "logs");
        _logger = new FileLogger(logDirectory, "app");
        _logger.Info("Switch Achievements starting (v0.1.0)");

        try
        {
            // 3. Settings.
            var settingsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SwitchAchievements");
            _settings = new SettingsService(settingsDirectory, _logger);

            // 3a. Start with Windows: make the current-user Run entry match the saved
            //     setting (also fixes a stale path). Never prevents startup.
            _startupRegistration = new StartupRegistration(_logger);
            try
            {
                _startupRegistration.Apply(_settings.Current.StartWithWindows);
            }
            catch (Exception ex)
            {
                _logger.Warn("could not update the Windows startup registration: " + ex.GetType().Name + ": " + ex.Message);
            }

            // 4. Activation listener (immediately after the mutex, before slow work).
            _activation = new ActivationListener(
                ActivationEventName,
                onActivated: () =>
                {
                    if (Volatile.Read(ref _shuttingDown))
                        return;
                    Dispatcher.BeginInvoke(ShowAndActivateMainWindow);
                },
                onError: ex => _logger?.Error("activation callback failed: " + ex.Message));
            _activation.Start();

            // 5. Monitoring host: loads the supported-game catalog once. A catalog that
            //    fails to load is logged and only makes game identification unavailable.
            _monitoring = new MonitoringHost(_logger);
            _shutdown = new ShutdownCoordinator(_monitoring, _logger);

            // 6. View model + window.
            //    The dashboard is created before monitoring starts, so it observes every state.
            //    Its refreshes are marshalled to this (UI) thread through the dispatcher.
            _dashboard = new DashboardViewModel(_monitoring, action => Dispatcher.BeginInvoke(action), TimeProvider.System);
            var mainViewModel = new MainViewModel(_settings, _dashboard) { ApplicationStatus = "Ready" };
            mainViewModel.OpenSettingsRequested += OpenSettings;
            _mainWindow = new MainWindow { DataContext = mainViewModel };
            _mainWindow.ExitRequested += RequestExit; // closing the window when close-to-tray is off
            _mainWindow.IsVisibleChanged += (_, args) => _dashboard?.SetViewVisible((bool)args.NewValue);

            // 7. Tray icon: live status from the dashboard (which already follows state and
            //    service changes on the UI thread), Restart through the restart coordinator,
            //    and native notifications from the notification monitor.
            _restart = new MonitoringRestartCoordinator(_monitoring, () => Volatile.Read(ref _shuttingDown), _logger);
            _tray = new TrayIconController(LoadTrayIcon(), ShowAndActivateMainWindow, OnRestartRequested, RequestExit);
            _restart.Changed += UpdateTray;
            _dashboard.PropertyChanged += OnDashboardPropertyChanged;
            _trayAttached = true;
            UpdateTray();

            _notifications = new NotificationMonitor(
                _monitoring,
                new TrayNotificationPolicy(),
                action => Dispatcher.BeginInvoke(action),
                IsMainWindowHidden,
                notification => _tray?.ShowNotification(notification),
                _logger);

            // 7a. "Enable live tracking" (M5.8.2): only a confirmed request writes the game's
            //     longshot_env.txt, off the UI thread, for the process that was identified.
            //     A dedicated Longshot integration supplies the candidate filter (crash helpers
            //     excluded) for re-selecting the process; it never connects.
            _setupIntegration = new Longshot.LongshotGameIntegration();
            var setup = new Longshot.LongshotLiveTrackingSetup(
                _setupIntegration, new Longshot.WindowsProcessProvider(), line => _logger?.Info(line));
            var monitoring = _monitoring;
            _liveTrackingSetup = new LiveTrackingSetupCoordinator(
                setup.Enable,
                ConfirmEnableLiveTracking,
                game => LiveTrackingEligibility.IsStillEligible(monitoring, game),
                _logger);
            _liveTrackingSetup.Changed += OnLiveTrackingSetupChanged;
            _dashboard.EnableLiveTrackingRequested += OnEnableLiveTrackingRequested;

            // 8. Show the window unless configured to start hidden.
            if (_settings.Current.ShowWindowOnLaunch)
            {
                ShowAndActivateMainWindow();
            }

            // 9. Start monitoring automatically, after the window and tray exist, whether or
            //    not the window is visible. The host never needs the UI thread (all of its
            //    awaits use ConfigureAwait(false), and starting composes synchronously), so
            //    waiting here cannot deadlock; failures reach the startup error handler below.
            _monitoring.StartAsync().GetAwaiter().GetResult();

            _logger.Info("ready");
        }
        catch (Exception ex)
        {
            _logger.Error("startup failed: " + ex);
            System.Windows.MessageBox.Show(
                "Switch Achievements could not start.\n\n" + ex.Message,
                "Switch Achievements",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Volatile.Write(ref _shuttingDown, true);
        DisposeServices();
        base.OnExit(e);
    }

    private void ShowAndActivateMainWindow()
    {
        if (Volatile.Read(ref _shuttingDown))
            return;
        _mainWindow?.ShowFromTray();
    }

    private void OpenSettings()
    {
        if (_settings is null || _mainWindow is null)
            return;

        var viewModel = new SettingsViewModel(_settings, _startupRegistration);
        var window = new SettingsWindow(viewModel) { Owner = _mainWindow };
        window.ShowDialog();
    }

    /// <summary>
    /// The explicit exit (tray Exit, or closing the window when close-to-tray is off):
    /// hides the window, awaits the bounded monitoring shutdown without blocking the UI
    /// thread, then shuts the application down. Duplicate requests are ignored.
    /// </summary>
    private void RequestExit()
    {
        if (_isShuttingDown)
            return;
        _isShuttingDown = true;
        Volatile.Write(ref _shuttingDown, true);

        _logger?.Info("shutting down");

        // Notifications are silenced first — the coordinator is about to hide the window,
        // which would otherwise count as "hidden" — then the tray stops following the
        // dashboard, and the dashboard goes, so no UI refresh can observe monitoring being
        // torn down.
        _notifications?.Dispose();
        StopLiveTrackingSetup(); // M5.8.2: cancel setup that has not started writing
        DetachTray();
        _dashboard?.Dispose();

        if (_shutdown is null)
        {
            _mainWindow?.AllowClose();
            Shutdown();
            return;
        }

        // The coordinator never throws; its task needs no observation.
        _ = _shutdown.RequestExitAsync(
            hideWindow: () => _mainWindow?.Hide(),
            shutdownApplication: () => Dispatcher.BeginInvoke(() =>
            {
                _mainWindow?.AllowClose();
                Shutdown();
            }));
    }

    private void DisposeServices()
    {
        // 0. Notifications, the tray's dashboard subscription, and the dashboard (all
        //    idempotent; already done on an explicit exit): unsubscribe from monitoring
        //    before monitoring shuts down.
        _notifications?.Dispose();
        StopLiveTrackingSetup();
        DetachTray();
        _dashboard?.Dispose();

        // 1. Monitoring: bounded; shares the stop of an explicit exit already in progress
        //    or completed, and is the safety net for every other exit path (e.g. log-off).
        _shutdown?.ShutdownFallback();

        // 2. Tray, 3. activation listener and other resources, 4. logger last.
        _tray?.Dispose();
        DisposeSetupIntegration();
        _activation?.Dispose();
        _guard?.Dispose();
        _logger?.Info("shutdown complete");
        _logger?.Dispose();
    }

    // ---------------- enable live tracking (M5.8.2) ----------------

    /// <summary>The explicit consent: a Yes/No warning with No as the default. Only Yes returns true. UI thread.</summary>
    private bool ConfirmEnableLiveTracking(Core.IdentifiedGame game)
    {
        if (Volatile.Read(ref _shuttingDown))
            return false;
        var owner = _mainWindow is { IsVisible: true } ? _mainWindow : null;
        var answer = owner is null
            ? System.Windows.MessageBox.Show(LiveTrackingConsent.Question, LiveTrackingConsent.Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : System.Windows.MessageBox.Show(owner, LiveTrackingConsent.Question, LiveTrackingConsent.Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    private async void OnEnableLiveTrackingRequested(Core.IdentifiedGame game)
    {
        if (_liveTrackingSetup is null)
            return;
        try
        {
            var result = await _liveTrackingSetup.RequestAsync(game);
            if (result is { } done && !Volatile.Read(ref _shuttingDown))
                _dashboard?.ShowLiveTrackingSetupResult(game, done);
        }
        catch (Exception ex)
        {
            _logger?.Error("enable live tracking request failed: " + ex.GetType().Name);
        }
    }

    private void OnLiveTrackingSetupChanged()
    {
        if (_liveTrackingSetup is not null && !Volatile.Read(ref _shuttingDown))
            _dashboard?.SetLiveTrackingSetupInProgress(_liveTrackingSetup.IsRunning);
    }

    /// <summary>The setup's integration never connects, so disposing it completes at once; bounded regardless.</summary>
    private void DisposeSetupIntegration()
    {
        try
        {
            _setupIntegration?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            _logger?.Warn("live tracking setup integration did not dispose cleanly: " + ex.GetType().Name);
        }
    }

    /// <summary>Cancels setup work that has not started writing and detaches it. Idempotent.</summary>
    private void StopLiveTrackingSetup()
    {
        if (_liveTrackingSetup is null)
            return;
        _liveTrackingSetup.Changed -= OnLiveTrackingSetupChanged;
        if (_dashboard is not null)
            _dashboard.EnableLiveTrackingRequested -= OnEnableLiveTrackingRequested;
        _liveTrackingSetup.Dispose();
    }

    /// <summary>The approved notification rule: the main window is hidden or minimized. UI thread.</summary>
    private bool IsMainWindowHidden()
        => _mainWindow is null
           || TrayNotificationPolicy.IsWindowHidden(_mainWindow.IsVisible, _mainWindow.WindowState == WindowState.Minimized);

    private void OnDashboardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.StatusSummary) or nameof(DashboardViewModel.MonitoringState))
            UpdateTray();
    }

    /// <summary>Pushes the dashboard's status (plus the restart state) to the tray. UI thread.</summary>
    private void UpdateTray()
    {
        if (_tray is null || _dashboard is null || _restart is null)
            return;
        _tray.Apply(TrayStatus.Compute(
            _dashboard.MonitoringState,
            _dashboard.StatusSummary,
            _restart.IsRunning,
            _restart.RecoveryRequired));
    }

    /// <summary>Stops the tray following the dashboard and the restart coordinator. Idempotent.</summary>
    private void DetachTray()
    {
        if (!_trayAttached)
            return;
        _trayAttached = false;
        if (_dashboard is not null)
            _dashboard.PropertyChanged -= OnDashboardPropertyChanged;
        if (_restart is not null)
            _restart.Changed -= UpdateTray;
    }

    /// <summary>Tray "Restart monitoring". The coordinator ignores duplicates and shutdown, and never throws.</summary>
    private async void OnRestartRequested()
    {
        if (_restart is null)
            return;
        try
        {
            var outcome = await _restart.RestartAsync();
            if (Volatile.Read(ref _shuttingDown))
                return;
            if (TrayNotificationPolicy.ForRestartOutcome(outcome, IsMainWindowHidden()) is { } notification)
                _tray?.ShowNotification(notification);
        }
        catch (Exception ex)
        {
            _logger?.Error("restart request failed: " + ex.GetType().Name);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("unhandled exception: " + e.Exception);
        e.Handled = true;
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            var icon = path is null ? null : Icon.ExtractAssociatedIcon(path);
            return icon ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }
}
