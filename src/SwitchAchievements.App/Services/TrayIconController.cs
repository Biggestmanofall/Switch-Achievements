using System.Drawing;
using System.Windows.Forms;

namespace SwitchAchievements.App.Services;

/// <summary>
/// Owns the system tray icon and its context menu. All WinForms interop is
/// contained here so the rest of the application never touches NotifyIcon
/// directly. Must be created and disposed on the WPF UI thread.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _monitoringStatusItem;
    private readonly ToolStripMenuItem _startMonitoringItem;
    private readonly ToolStripMenuItem _stopMonitoringItem;
    private bool _disposed;

    public TrayIconController(Icon icon, Action openRequested, Action exitRequested)
    {
        _menu = new ContextMenuStrip();
        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "Switch Achievements",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        var openItem = new ToolStripMenuItem("Open Switch Achievements");
        openItem.Click += (_, _) => openRequested();

        _monitoringStatusItem = new ToolStripMenuItem("Monitoring status: Unavailable") { Enabled = false };
        _startMonitoringItem = new ToolStripMenuItem("Start monitoring") { Enabled = false };
        _stopMonitoringItem = new ToolStripMenuItem("Stop monitoring") { Enabled = false };

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exitRequested();

        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_monitoringStatusItem);
        _menu.Items.Add(_startMonitoringItem);
        _menu.Items.Add(_stopMonitoringItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon.DoubleClick += (_, _) => openRequested();
    }

    public void UpdateState(MonitoringState state)
        => _monitoringStatusItem.Text = "Monitoring status: " + StateText(state);

    private static string StateText(MonitoringState state) => state switch
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}