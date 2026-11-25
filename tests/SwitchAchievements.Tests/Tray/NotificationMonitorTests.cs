using SwitchAchievements.App.Services;
using SwitchAchievements.Tests.Dashboard;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.MonitoringIdentity;
using static SwitchAchievements.Tests.Tray.TrayNotificationPolicyTests;

namespace SwitchAchievements.Tests.Tray;

/// <summary>
/// M5.7 Task 4: NotificationMonitor. Deterministic: scripted services, a host with a fake
/// factory, and a queued UI poster that the test drains explicitly.
/// </summary>
public class NotificationMonitorTests
{
    private sealed class Rig : IDisposable
    {
        public QueuedPoster Queue { get; } = new();
        public List<ScriptedMonitoringService> Services { get; } = new();
        public List<TrayNotification> Shown { get; } = new();
        public CapturingLogger Logger { get; } = new();
        public Action<ScriptedMonitoringService>? OnCreate { get; set; }
        public bool Hidden { get; set; } = true;
        public int VisibilityReads { get; private set; }
        public MonitoringHost Host { get; }
        public NotificationMonitor Monitor { get; }

        public Rig()
        {
            Host = new MonitoringHost(Logger,
                serviceFactory: _ =>
                {
                    var s = new ScriptedMonitoringService();
                    OnCreate?.Invoke(s);
                    Services.Add(s);
                    return s;
                },
                catalogLoader: () => IdentityTestData.OotCatalog());
            Monitor = new NotificationMonitor(Host, new TrayNotificationPolicy(), Queue.Post,
                () => { VisibilityReads++; return Hidden; }, Shown.Add, Logger);
        }

        public ScriptedMonitoringService Service => Services[^1];

        public List<TrayNotificationKind> Kinds => Shown.Select(n => n.Kind).ToList();

        public void Dispose() => Monitor.Dispose();
    }

    [Fact]
    public async Task Subscribes_to_the_started_service_and_notifies_a_detected_game()
    {
        using var rig = new Rig();
        await rig.Host.StartAsync();
        rig.Queue.RunAll();
        Assert.Equal(1, rig.Service.Subscribers);

        rig.Service.CurrentIdentification = Identify("identified");
        rig.Service.Set(MonitoringState.Monitoring);
        rig.Queue.RunAll();

        Assert.Equal(new[] { TrayNotificationKind.SupportedGameDetected }, rig.Kinds);
    }

    [Fact]
    public async Task Every_event_is_posted_separately_and_evaluated_in_order()
    {
        using var rig = new Rig();
        await rig.Host.StartAsync();
        rig.Queue.RunAll();

        rig.Service.Set(MonitoringState.Connecting);
        rig.Service.CurrentIdentification = Identify("unsupported");
        rig.Service.Set(MonitoringState.Monitoring);
        rig.Service.CurrentIdentification = null;
        rig.Service.Set(MonitoringState.Error);

        Assert.Equal(3, rig.Queue.Pending); // not coalesced
        rig.Queue.RunAll();

        Assert.Equal(new[] { TrayNotificationKind.UnsupportedGameDetected, TrayNotificationKind.MonitoringError }, rig.Kinds);
    }

    [Fact]
    public async Task Identification_is_captured_with_the_event_before_it_is_cleared()
    {
        using var rig = new Rig();
        await rig.Host.StartAsync();
        rig.Queue.RunAll();

        rig.Service.CurrentIdentification = Identify("identified");
        rig.Service.Set(MonitoringState.Monitoring);
        // The service fails before the UI thread runs: identification is cleared on Error.
        rig.Service.CurrentIdentification = null;
        rig.Service.Set(MonitoringState.Error);
        rig.Queue.RunAll();

        Assert.Equal(new[] { TrayNotificationKind.SupportedGameDetected, TrayNotificationKind.MonitoringError }, rig.Kinds);
    }

    [Fact]
    public async Task Visibility_is_evaluated_when_the_notification_is_due()
    {
        using var rig = new Rig();
        await rig.Host.StartAsync();
        rig.Queue.RunAll();

        rig.Hidden = true; // hidden when the event happens...
        rig.Service.CurrentIdentification = Identify("identified");
        rig.Service.Set(MonitoringState.Monitoring);
        rig.Hidden = false; // ...but shown again before the UI thread evaluates it
        rig.Queue.RunAll();
        Assert.Empty(rig.Shown);

        // Consumed while visible: hiding the window later does not replay it.
        rig.Hidden = true;
        rig.Service.Set(MonitoringState.Reconnecting);
        rig.Service.Set(MonitoringState.Connecting);
        rig.Service.Set(MonitoringState.Monitoring);
        rig.Queue.RunAll();
        Assert.Empty(rig.Shown);
    }

    [Fact]
    public async Task Service_replacement_detaches_the_old_service_and_ignores_its_stale_events()
    {
        using var rig = new Rig();
        await rig.Host.StartAsync();
        var old = rig.Service;
        old.Set(MonitoringState.Error);
        rig.Queue.RunAll();
        Assert.Equal(new[] { TrayNotificationKind.MonitoringError }, rig.Kinds);

        Assert.True(await rig.Host.RestartAsync());
        var replacement = rig.Service;
        Assert.NotSame(old, replacement);
        Assert.Equal(0, old.Subscribers);
        Assert.Equal(1, replacement.Subscribers);

        // A late event from the old service (raised directly, as a stale delivery would be).
        old.CurrentIdentification = Identify("identified");
        old.State = MonitoringState.Monitoring;
        old.RaiseStateChanged(MonitoringState.Monitoring); // no subscriber: nothing posted
        rig.Queue.RunAll();

        // The new service can report its own error.
        replacement.Set(MonitoringState.Error);
        rig.Queue.RunAll();
        Assert.Equal(new[] { TrayNotificationKind.MonitoringError, TrayNotificationKind.MonitoringError }, rig.Kinds);
    }

    [Fact]
    public async Task Events_posted_by_a_service_that_was_replaced_before_the_ui_ran_are_ignored()
    {
        using var rig = new Rig();
        await rig.Host.StartAsync();
        rig.Queue.RunAll();
        var old = rig.Service;

        old.CurrentIdentification = Identify("identified");
        old.Set(MonitoringState.Monitoring); // posted, not yet evaluated
        Assert.True(await rig.Host.RestartAsync());
        rig.Queue.RunAll();

        Assert.Empty(rig.Shown);
    }

    [Fact]
    public async Task An_event_raised_before_the_host_publishes_the_service_is_still_evaluated()
    {
        using var rig = new Rig
        {
            // The service reaches Error during its start, before ServiceChanged; no one is
            // subscribed yet, so the event itself is lost.
            OnCreate = s => s.Set(MonitoringState.Error),
        };

        await rig.Host.StartAsync();
        rig.Queue.RunAll();

        Assert.Equal(new[] { TrayNotificationKind.MonitoringError }, rig.Kinds);
    }

    [Fact]
    public async Task Repeated_evaluations_of_the_same_state_are_harmless()
    {
        using var rig = new Rig { OnCreate = s => { s.State = MonitoringState.Error; } };
        await rig.Host.StartAsync();
        rig.Service.RaiseStateChanged(MonitoringState.Error); // the same Error, seen again
        rig.Queue.RunAll();

        Assert.Equal(new[] { TrayNotificationKind.MonitoringError }, rig.Kinds);
    }

    [Fact]
    public async Task Mute_suppresses_notifications_including_already_posted_ones()
    {
        using var rig = new Rig();
        await rig.Host.StartAsync();
        rig.Queue.RunAll();

        rig.Service.Set(MonitoringState.Error); // posted
        rig.Monitor.Mute();
        rig.Queue.RunAll();

        Assert.True(rig.Monitor.IsMuted);
        Assert.Empty(rig.Shown);
    }

    [Fact]
    public async Task Dispose_detaches_from_the_host_and_the_service()
    {
        var rig = new Rig();
        await rig.Host.StartAsync();
        var service = rig.Service;
        Assert.Equal(1, service.Subscribers);

        rig.Monitor.Dispose();
        rig.Monitor.Dispose(); // idempotent
        Assert.Equal(0, service.Subscribers);

        Assert.True(await rig.Host.RestartAsync());
        Assert.Equal(0, rig.Service.Subscribers);
        rig.Service.Set(MonitoringState.Error);
        rig.Queue.RunAll();
        Assert.Empty(rig.Shown);
    }

    [Fact]
    public async Task Stopping_the_host_releases_the_service()
    {
        using var rig = new Rig();
        await rig.Host.StartAsync();
        var service = rig.Service;

        Assert.True(await rig.Host.StopAsync());
        Assert.Equal(0, service.Subscribers);
    }

    [Fact]
    public async Task A_failing_show_is_logged_and_does_not_break_later_notifications()
    {
        var shown = new List<TrayNotification>();
        var calls = 0;
        var logger = new CapturingLogger();
        var services = new List<ScriptedMonitoringService>();
        var host = new MonitoringHost(logger,
            serviceFactory: _ => { var s = new ScriptedMonitoringService(); services.Add(s); return s; },
            catalogLoader: () => IdentityTestData.OotCatalog());
        using var monitor = new NotificationMonitor(host, new TrayNotificationPolicy(), a => a(), () => true,
            n => { if (calls++ == 0) throw new InvalidOperationException("tray gone"); shown.Add(n); }, logger);

        await host.StartAsync();
        services[^1].Set(MonitoringState.Error); // show throws
        Assert.True(await host.RestartAsync());
        services[^1].Set(MonitoringState.Error);

        Assert.Single(shown);
        Assert.Equal(1, logger.Count("tray notification failed"));
    }
}
