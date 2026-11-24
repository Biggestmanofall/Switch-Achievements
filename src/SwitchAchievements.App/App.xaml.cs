using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.App.Infrastructure.Settings;
using SwitchAchievements.App.Infrastructure.SingleInstance;
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

            // 5. View model + window.
            var mainViewModel = new MainViewModel(_settings) { ApplicationStatus = "Ready" };
            mainViewModel.OpenSettingsRequested += OpenSettings;
            _mainWindow = new MainWindow { DataContext = mainViewModel };

            // 6. Tray icon.
            _tray = new TrayIconController(LoadTrayIcon(), ShowAndActivateMainWindow, RequestExit);
            _tray.UpdateState(MonitoringState.Stopped);

            // 7. Show the window unless configured to start hidden.
            if (_settings.Current.ShowWindowOnLaunch)
            {
                ShowAndActivateMainWindow();
            }

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

        var viewModel = new SettingsViewModel(_settings);
        var window = new SettingsWindow(viewModel) { Owner = _mainWindow };
        window.ShowDialog();
    }

    private void RequestExit()
    {
        if (_isShuttingDown)
            return;
        _isShuttingDown = true;
        Volatile.Write(ref _shuttingDown, true);

        _logger?.Info("shutting down");
        _mainWindow?.AllowClose();
        Shutdown();
    }

    private void DisposeServices()
    {
        _activation?.Dispose();
        _tray?.Dispose();
        _logger?.Info("shutdown complete");
        _logger?.Dispose();
        _guard?.Dispose();
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
