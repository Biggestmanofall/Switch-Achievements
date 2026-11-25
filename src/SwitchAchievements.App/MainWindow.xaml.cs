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
        SizeChanged += OnSizeChanged;
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    // The standard Windows title bar, asked to use its dark variant so it matches the
    // dashboard (M5.7 Task 3). Best effort: ignored where unsupported.
    private void UseDarkTitleBar()
    {
        const int DwmwaUseImmersiveDarkMode = 20;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var enabled = 1;
        try
        {
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Tile columns at or above this window width; narrower windows use two columns.</summary>
    private const double ThreeColumnMinWidth = 1000;

    // Responsive tiles (M5.7 Task 3): three columns on wide windows, two on narrow ones.
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        => TileGrid.Columns = e.NewSize.Width >= ThreeColumnMinWidth ? 3 : 2;

    /// <summary>
    /// Raised when closing the window should exit the application (close-to-tray is
    /// off). The application handles it with its coordinated shutdown, which stops
    /// monitoring before exiting; the window never owns or stops monitoring itself.
    /// </summary>
    public event Action? ExitRequested;

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
        // Read the active settings now, so a change saved in Settings applies immediately.
        var minimizeToTray = (DataContext as MainViewModel)?.Settings.Current.MinimizeToTrayOnClose == true;
        var exit = ExitRequested;

        switch (MainWindowClosePolicy.Decide(_allowClose, minimizeToTray, exit is not null))
        {
            case MainWindowCloseAction.Close:
                Closing -= OnClosing;
                break;

            case MainWindowCloseAction.HideToTray:
                // Hide to the tray and keep running.
                e.Cancel = true;
                Hide();
                break;

            case MainWindowCloseAction.RequestExit:
                // Closing the window means exit the whole application — through the
                // application's coordinated shutdown, which stops monitoring first and
                // then closes this window (AllowClose).
                e.Cancel = true;
                exit!();
                break;

            case MainWindowCloseAction.ShutdownDirectly:
                // No coordinator attached: exit directly (App.OnExit still stops monitoring).
                _allowClose = true;
                System.Windows.Application.Current.Shutdown();
                break;
        }
    }
}
