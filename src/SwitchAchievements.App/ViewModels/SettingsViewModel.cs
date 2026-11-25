using System.Windows.Input;
using SwitchAchievements.App.Infrastructure.Settings;
using SwitchAchievements.App.Infrastructure.Startup;
using SwitchAchievements.App.Mvvm;

namespace SwitchAchievements.App.ViewModels;

/// <summary>
/// View model for the settings window. Works on a copy of the settings and only
/// persists them when the user saves.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    /// <summary>Shown when the settings were saved but the Windows startup entry could not be updated.</summary>
    public const string StartupRegistrationFailedMessage =
        "Your settings were saved, but Switch Achievements could not update its Windows startup entry. "
        + "Try saving again; it will also retry the next time the application starts.";

    private readonly SettingsService _settingsService;
    private readonly IStartupRegistration? _startupRegistration;
    private bool _showWindowOnLaunch;
    private bool _minimizeToTrayOnClose;
    private bool _startWithWindows;
    private string? _errorMessage;

    /// <param name="settingsService">The application's settings.</param>
    /// <param name="startupRegistration">Applies <see cref="StartWithWindows"/> after saving; null skips it.</param>
    public SettingsViewModel(SettingsService settingsService, IStartupRegistration? startupRegistration = null)
    {
        _settingsService = settingsService;
        _startupRegistration = startupRegistration;
        _showWindowOnLaunch = settingsService.Current.ShowWindowOnLaunch;
        _minimizeToTrayOnClose = settingsService.Current.MinimizeToTrayOnClose;
        _startWithWindows = settingsService.Current.StartWithWindows;

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public bool ShowWindowOnLaunch
    {
        get => _showWindowOnLaunch;
        set => SetProperty(ref _showWindowOnLaunch, value);
    }

    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set => SetProperty(ref _minimizeToTrayOnClose, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    /// <summary>An error to show in the window; null when there is none.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public event Action? CloseRequested;

    private void Save()
    {
        _settingsService.Save(new AppSettings
        {
            ShowWindowOnLaunch = _showWindowOnLaunch,
            MinimizeToTrayOnClose = _minimizeToTrayOnClose,
            StartWithWindows = _startWithWindows,
        });

        if (_startupRegistration is not null)
        {
            try
            {
                _startupRegistration.Apply(_startWithWindows);
            }
            catch (Exception)
            {
                // The setting stays saved (startup retries it); the window stays open.
                ErrorMessage = StartupRegistrationFailedMessage;
                return;
            }
        }

        ErrorMessage = null;
        CloseRequested?.Invoke();
    }
}
