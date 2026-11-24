using System.ComponentModel;
using System.Windows;
using SwitchAchievements.App.ViewModels;

namespace SwitchAchievements.App;

/// <summary>
/// Main application window. Intercepts the close event so that closing minimizes
/// to the system tray (per settings) instead of exiting, unless the application
/// is explicitly shutting down.
/// </summary>
public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    /// <summary>Allows the next close to complete (used during app shutdown).</summary>
    public void AllowClose() => _allowClose = true;

    /// <summary>Shows the window, restores it from minimized, and activates it.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            Closing -= OnClosing;
            return;
        }

        var viewModel = DataContext as MainViewModel;
        if (viewModel?.Settings.Current.MinimizeToTrayOnClose == true)
        {
            // Hide to the tray and keep running.
            e.Cancel = true;
            Hide();
        }
        else
        {
            // Closing the window means exit the whole application.
            _allowClose = true;
            System.Windows.Application.Current.Shutdown();
        }
    }
}
