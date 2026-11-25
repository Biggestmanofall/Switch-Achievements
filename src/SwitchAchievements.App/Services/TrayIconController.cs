using System.Drawing;
using System.Windows.Forms;

namespace SwitchAchievements.App.Services;

/// <summary>
/// Owns the system tray icon, its context menu, and its native notifications. All
/// WinForms interop is contained here so the rest of the application never touches
/// NotifyIcon directly. Must be created, updated, and disposed on the WPF UI thread.
///
/// <para>Menu: Open | live status (disabled) | Restart monitoring (only when
/// appropriate) | Exit. There is deliberately no Pause or Stop.</para>
/// </summary>
public sealed class TrayIconController : IDisposable
{
    /// <summary>How long a notification asks to stay up (Windows may override it).</summary>
    private const int NotificationTimeoutMs = 5000;

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _restartItem;
    private bool _disposed;

    public TrayIconController(Icon icon, Action openRequested, Action restartRequested, Action exitRequested)
    {
        ArgumentNullException.ThrowIfNull(openRequested);
        ArgumentNullException.ThrowIfNull(restartRequested);
        ArgumentNullException.ThrowIfNull(exitRequested);

        _menu = new ContextMenuStrip();
        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = TrayStatus.ApplicationName,
            Visible = true,
            ContextMenuStrip = _menu,
        };

        var openItem = new ToolStripMenuItem("Open Switch Achievements");
        openItem.Click += (_, _) => openRequested();

        _statusItem = new ToolStripMenuItem(TrayStatus.ApplicationName) { Enabled = false };

        _restartItem = new ToolStripMenuItem("Restart monitoring") { Visible = false, Enabled = false };
        _restartItem.Click += (_, _) => restartRequested();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exitRequested();

        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(_restartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon.DoubleClick += (_, _) => openRequested();
        _notifyIcon.BalloonTipClicked += (_, _) => openRequested();
    }

    /// <summary>Applies the live status and the Restart item's availability.</summary>
    public void Apply(TrayViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_disposed)
            return;

        // '&' would otherwise be taken as a menu mnemonic (e.g. in a game's name).
        _statusItem.Text = state.StatusText.Replace("&", "&&", StringComparison.Ordinal);
        _notifyIcon.Text = state.Tooltip.Length <= TrayStatus.MaxTooltipLength
            ? state.Tooltip
            : TrayStatus.Tooltip(state.StatusText);
        _restartItem.Visible = state.RestartVisible;
        _restartItem.Enabled = state.RestartEnabled;
    }

    /// <summary>Shows a native Windows notification from the tray icon.</summary>
    public void ShowNotification(TrayNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (_disposed)
            return;

        _notifyIcon.ShowBalloonTip(
            NotificationTimeoutMs,
            notification.Title,
            notification.Text,
            notification.IsError ? ToolTipIcon.Error : ToolTipIcon.Info);
    }

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
