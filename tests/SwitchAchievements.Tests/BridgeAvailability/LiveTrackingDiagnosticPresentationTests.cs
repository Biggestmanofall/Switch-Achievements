using System.Xml.Linq;
using SwitchAchievements.App.Services;
using SwitchAchievements.App.ViewModels;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Dashboard;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.LongshotIdentity;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.BridgeAvailability;

/// <summary>M5.8.1: how LiveTrackingDisabled reaches the dashboard, the tray, and notifications.</summary>
public class LiveTrackingDiagnosticPresentationTests
{
    private const string GamePath = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private const string OotName = "The Legend of Zelda: Ocarina of Time";
    private static readonly GameProcessInfo Process100 = new(100, GamePath, new DateTime(2026, 11, 24, 5, 0, 0, DateTimeKind.Utc));

    private sealed class Rig : IDisposable
    {
        public List<ScriptedMonitoringService> Services { get; } = new();
        public MonitoringHost Host { get; }
        public DashboardViewModel Vm { get; private set; } = null!;
        public List<string?> Changed { get; } = new();

        public Rig()
        {
            Host = new MonitoringHost(new CapturingLogger(),
                serviceFactory: _ => { var s = new ScriptedMonitoringService(); Services.Add(s); return s; },
                catalogLoader: () => IdentityTestData.OotCatalog());
        }

        public ScriptedMonitoringService Service => Services[^1];

        public async Task<Rig> StartAsync(bool visible)
        {
            await Host.StartAsync();
            Vm = new DashboardViewModel(Host, a => a(), new ManualTimeProvider());
            Vm.PropertyChanged += (_, e) => Changed.Add(e.PropertyName);
            Vm.SetViewVisible(visible);
            return this;
        }

        public void Dispose() => Vm?.Dispose();
    }

    private static GameIdentification Identified()
        => GameIdentityResolver.Resolve(IdentityTestData.OotCatalog(), IdentityEvidence.For("identified", Process100));

    /// <summary>The service as it is while the bridge is absent: Connecting, identified, diagnostic raised.</summary>
    private static void Disable(ScriptedMonitoringService service)
    {
        service.CurrentIdentification = Identified();
        service.Set(MonitoringState.Connecting);
        service.SetDiagnostic(MonitoringDiagnostic.LiveTrackingDisabled);
    }

    // ---------------- dashboard ----------------

    [Fact]
    public async Task Disabled_live_tracking_is_explained_while_the_state_stays_connecting()
    {
        using var rig = await new Rig().StartAsync(visible: true);
        Disable(rig.Service);

        var vm = rig.Vm;
        Assert.Equal(MonitoringState.Connecting, vm.MonitoringState);
        Assert.Equal(MonitoringDiagnostic.LiveTrackingDisabled, vm.Diagnostic);
        Assert.True(vm.HasDiagnostic);
        Assert.Equal("Live tracking disabled", vm.MonitoringStatusText);
        Assert.Equal(ConnectionStatus.NotConnected, vm.Connection);
        Assert.Equal("Live tracking off", vm.ConnectionText);
        Assert.Equal("Live tracking is disabled for this game.", vm.FreshnessText);
        Assert.Equal(DashboardFormat.LiveTrackingDisabledHint, vm.DiagnosticHint);
        Assert.Equal("Live tracking disabled for this game", vm.StatusSummary);
        Assert.False(vm.IsStale);
        Assert.Null(vm.StaleHint);

        // The detected game stays visible.
        Assert.Equal(IdentificationDisplayStatus.Identified, vm.Identification);
        Assert.Equal(OotName, vm.IdentificationText);
        Assert.Equal(OotName, vm.GameDisplayName);
    }

    [Fact]
    public void The_hint_wording_is_the_approved_text()
        => Assert.Equal(
            "Longshot's live data bridge isn't enabled for this game, so gameplay data can't be read. It can't be enabled while the game is running.",
            DashboardFormat.LiveTrackingDisabledHint);

    [Fact]
    public async Task A_diagnostic_change_alone_refreshes_the_dashboard_even_while_hidden()
    {
        using var rig = await new Rig().StartAsync(visible: false);
        rig.Service.Set(MonitoringState.Connecting);
        rig.Changed.Clear();

        rig.Service.SetDiagnostic(MonitoringDiagnostic.LiveTrackingDisabled); // no state change

        Assert.True(rig.Vm.HasDiagnostic);
        Assert.Contains(nameof(DashboardViewModel.HasDiagnostic), rig.Changed);
        Assert.Contains(nameof(DashboardViewModel.StatusSummary), rig.Changed);
        Assert.Contains(nameof(DashboardViewModel.DiagnosticHint), rig.Changed);
    }

    [Fact]
    public async Task Clearing_the_diagnostic_restores_the_connecting_presentation()
    {
        using var rig = await new Rig().StartAsync(visible: true);
        Disable(rig.Service);

        rig.Service.SetDiagnostic(MonitoringDiagnostic.None);

        var vm = rig.Vm;
        Assert.False(vm.HasDiagnostic);
        Assert.Null(vm.DiagnosticHint);
        Assert.Equal("Connecting", vm.MonitoringStatusText);
        Assert.Equal(ConnectionStatus.Connecting, vm.Connection);
        Assert.Equal("Connecting", vm.ConnectionText);
        Assert.Equal("Connecting to Longshot", vm.StatusSummary);
    }

    [Theory]
    [InlineData(MonitoringState.Searching)]
    [InlineData(MonitoringState.Monitoring)]
    [InlineData(MonitoringState.Reconnecting)]
    [InlineData(MonitoringState.Error)]
    public async Task A_diagnostic_outside_connecting_is_never_shown(MonitoringState state)
    {
        using var rig = await new Rig().StartAsync(visible: true);
        rig.Service.Diagnostic = MonitoringDiagnostic.LiveTrackingDisabled;
        rig.Service.Set(state);

        Assert.False(rig.Vm.HasDiagnostic);
        Assert.Null(rig.Vm.DiagnosticHint);
        Assert.NotEqual(DashboardFormat.LiveTrackingDisabledSummary, rig.Vm.StatusSummary);
    }

    [Fact]
    public async Task Replacing_the_service_drops_the_old_diagnostic_and_its_subscription()
    {
        using var rig = await new Rig().StartAsync(visible: true);
        var old = rig.Service;
        Disable(old);
        Assert.Equal(1, old.DiagnosticSubscribers);

        Assert.True(await rig.Host.RestartAsync());
        var replacement = rig.Service;

        Assert.NotSame(old, replacement);
        Assert.False(rig.Vm.HasDiagnostic);
        Assert.Equal(0, old.DiagnosticSubscribers);
        Assert.Equal(1, replacement.DiagnosticSubscribers);

        // A late diagnostic event from the old service changes nothing.
        old.SetDiagnostic(MonitoringDiagnostic.LiveTrackingDisabled);
        Assert.False(rig.Vm.HasDiagnostic);
    }

    [Fact]
    public async Task Stopping_monitoring_clears_the_presentation()
    {
        using var rig = await new Rig().StartAsync(visible: true);
        Disable(rig.Service);

        Assert.True(await rig.Host.StopAsync());

        Assert.False(rig.Vm.HasDiagnostic);
        Assert.Equal(MonitoringState.Stopped, rig.Vm.MonitoringState);
    }

    [Fact]
    public async Task Disposal_unsubscribes_the_diagnostic_handler()
    {
        var rig = await new Rig().StartAsync(visible: true);
        var service = rig.Service;
        Assert.Equal(1, service.DiagnosticSubscribers);

        rig.Vm.Dispose();

        Assert.Equal(0, service.DiagnosticSubscribers);
        Assert.Equal(0, service.Subscribers);
    }

    // ---------------- format ----------------

    [Fact]
    public void The_summary_uses_the_diagnostic_only_while_connecting()
    {
        Assert.Equal("Live tracking disabled for this game",
            DashboardFormat.StatusSummary(MonitoringState.Connecting, IdentificationDisplayStatus.Identified, OotName, MonitoringDiagnostic.LiveTrackingDisabled));
        Assert.Equal("Monitoring — " + OotName,
            DashboardFormat.StatusSummary(MonitoringState.Monitoring, IdentificationDisplayStatus.Identified, OotName, MonitoringDiagnostic.LiveTrackingDisabled));
        Assert.Equal("Connecting to Longshot",
            DashboardFormat.StatusSummary(MonitoringState.Connecting, IdentificationDisplayStatus.Identified, OotName));
    }

    // ---------------- tray ----------------

    [Fact]
    public async Task The_tray_tooltip_explains_disabled_live_tracking_and_offers_no_restart()
    {
        using var rig = await new Rig().StartAsync(visible: false);
        Disable(rig.Service);

        var tray = TrayStatus.Compute(rig.Vm.MonitoringState, rig.Vm.StatusSummary, restartInProgress: false, restartFailed: false);

        Assert.Equal("Live tracking disabled for this game", tray.StatusText);
        Assert.Equal("Switch Achievements — Live tracking disabled for this game", tray.Tooltip);
        Assert.False(tray.RestartVisible);
        Assert.False(tray.RestartEnabled);
    }

    // ---------------- notifications ----------------

    [Fact]
    public void The_policy_never_notifies_for_connecting()
        => Assert.Null(new TrayNotificationPolicy().Observe(new object(), MonitoringState.Connecting, Identified(), windowHidden: true));

    [Fact]
    public async Task No_notification_fires_while_live_tracking_is_disabled()
    {
        using var rig = await new Rig().StartAsync(visible: false);
        var shown = new List<TrayNotification>();
        using var monitor = new NotificationMonitor(rig.Host, new TrayNotificationPolicy(), a => a(), () => true, shown.Add, new CapturingLogger());

        Disable(rig.Service);
        rig.Service.SetDiagnostic(MonitoringDiagnostic.LiveTrackingDisabled);

        Assert.Empty(shown);
    }

    // ---------------- window (structure of MainWindow.xaml) ----------------

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument MainWindowXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SwitchAchievements.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir.FullName, "src", "SwitchAchievements.App", "MainWindow.xaml"));
    }

    [Fact]
    public void The_connecting_pulse_is_suppressed_while_a_diagnostic_is_shown()
    {
        var pulseStyle = MainWindowXaml().Descendants(Presentation + "Style")
            .Single(s => (string?)s.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "PulseRingStyle");

        // No unconditional Connecting trigger remains...
        Assert.DoesNotContain(pulseStyle.Descendants(Presentation + "DataTrigger"),
            t => ((string?)t.Attribute("Value")) == "Connecting");

        // ...the Connecting pulse requires HasDiagnostic == False...
        var multi = Assert.Single(pulseStyle.Descendants(Presentation + "MultiDataTrigger"));
        var conditions = multi.Descendants(Presentation + "Condition")
            .Select(c => ((string?)c.Attribute("Binding"), (string?)c.Attribute("Value"))).ToArray();
        Assert.Contains(("{Binding Dashboard.MonitoringState}", "Connecting"), conditions);
        Assert.Contains(("{Binding Dashboard.HasDiagnostic}", "False"), conditions);
        Assert.Contains(multi.Descendants(Presentation + "BeginStoryboard"), b => (string?)b.Attribute("Name") == "PulseConnecting");

        // ...and the Searching and Reconnecting pulses are unchanged.
        var plain = pulseStyle.Descendants(Presentation + "DataTrigger").Select(t => (string?)t.Attribute("Value")).ToArray();
        Assert.Equal(new[] { "Searching", "Reconnecting" }, plain);
    }

    [Fact]
    public void The_freshness_strip_has_a_hint_row_bound_to_the_diagnostic()
    {
        var doc = MainWindowXaml();
        var hint = Assert.Single(doc.Descendants(Presentation + "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding Dashboard.DiagnosticHint}");
        Assert.Equal("Wrap", (string?)hint.Attribute("TextWrapping"));

        var row = hint.Parent!;
        Assert.Contains(row.Descendants(Presentation + "DataTrigger"),
            t => (string?)t.Attribute("Binding") == "{Binding Dashboard.HasDiagnostic}" && (string?)t.Attribute("Value") == "True");
    }
}
