using System.Windows.Input;
using SwitchAchievements.App.Infrastructure.Settings;
using SwitchAchievements.App.Mvvm;

namespace SwitchAchievements.App.ViewModels;

/// <summary>
/// View model for the settings window. Works on a copy of the settings and only
/// persists them when the user saves.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private bool _showWindowOnLaunch;
    private bool _minimizeToTrayOnClose;

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _showWindowOnLaunch = settingsService.Current.ShowWindowOnLaunch;
        _minimizeToTrayOnClose = settingsService.Current.MinimizeToTrayOnClose;

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

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public event Action? CloseRequested;

    private void Save()
    {
        _settingsService.Save(new AppSettings
        {
            ShowWindowOnLaunch = _showWindowOnLaunch,
            MinimizeToTrayOnClose = _minimizeToTrayOnClose,
        });
        CloseRequested?.Invoke();
    }
}