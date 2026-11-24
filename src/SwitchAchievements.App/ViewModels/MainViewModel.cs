using System.Windows.Input;
using SwitchAchievements.App.Infrastructure.Settings;
using SwitchAchievements.App.Mvvm;
using SwitchAchievements.App.Services;

namespace SwitchAchievements.App.ViewModels;

/// <summary>
/// View model for the main window. Exposes static status surfaces only — real
/// detection/monitoring data is produced by M5. Deliberately free of WPF types
/// so it can be unit-tested without a UI thread.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private string _applicationStatus = "Starting";
    private string _integrationStatus = "Not connected";
    private MonitoringState _monitoringState = MonitoringState.Stopped;

    public MainViewModel(SettingsService settings)
    {
        Settings = settings;
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke());
    }

    public SettingsService Settings { get; }

    public string ApplicationName => "Switch Achievements";

    public string Version => "0.1.0";

    public string IntegrationName => "Longshot (recompilation)";

    public string ApplicationStatus
    {
        get => _applicationStatus;
        set => SetProperty(ref _applicationStatus, value);
    }

    public string IntegrationStatus
    {
        get => _integrationStatus;
        set => SetProperty(ref _integrationStatus, value);
    }

    public MonitoringState MonitoringState
    {
        get => _monitoringState;
        set
        {
            if (SetProperty(ref _monitoringState, value))
                OnPropertyChanged(nameof(MonitoringStatusText));
        }
    }

    public string MonitoringStatusText => _monitoringState switch
    {
        MonitoringState.Stopped => "Stopped",
        MonitoringState.Searching => "Searching",
        MonitoringState.Connecting => "Connecting",
        MonitoringState.Monitoring => "Monitoring",
        MonitoringState.Reconnecting => "Reconnecting",
        MonitoringState.Ambiguous => "Ambiguous",
        MonitoringState.Error => "Error",
        _ => "Unknown",
    };

    public ICommand OpenSettingsCommand { get; }

    public event Action? OpenSettingsRequested;
}