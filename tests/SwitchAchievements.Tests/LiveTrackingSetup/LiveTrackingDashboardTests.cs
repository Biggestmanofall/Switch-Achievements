using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SwitchAchievements.App.Services;
using SwitchAchievements.App.ViewModels;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;
using SwitchAchievements.Tests.Dashboard;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.LongshotIdentity;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.LiveTrackingSetup;

/// <summary>M5.8.2: the dashboard's "Enable live tracking" action and result presentation.</summary>
public class LiveTrackingDashboardTests
{
    private const string GamePath = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private const string OotName = "The Legend of Zelda: Ocarina of Time";
    private static readonly DateTime Start = new(2026, 11, 24, 5, 0, 0, DateTimeKind.Utc);
    private static readonly GameProcessInfo Process100 = new(100, GamePath, Start);
    private static readonly GameProcessInfo Process200 = new(200, GamePath, Start);

    private static GameIdentification Identify(string kind, GameProcessInfo? process = null)
        => GameIdentityResolver.Resolve(IdentityTestData.OotCatalog(), IdentityEvidence.For(kind, process ?? Process100));

    private sealed class Rig : IDisposable
    {
        public List<ScriptedMonitoringService> Services { get; } = new();
        public MonitoringHost Host { get; }
        public DashboardViewModel Vm { get; private set; } = null!;
        public List<IdentifiedGame> Requests { get; } = new();
        public int CanExecuteChanges { get; private set; }

        public Rig()
        {
            Host = new MonitoringHost(new CapturingLogger(),
                serviceFactory: _ => { var s = new ScriptedMonitoringService(); Services.Add(s); return s; },
                catalogLoader: () => IdentityTestData.OotCatalog());
        }

        public ScriptedMonitoringService Service => Services[^1];

        public async Task<Rig> StartAsync()
        {
            await Host.StartAsync();
            Vm = new DashboardViewModel(Host, a => a(), new ManualTimeProvider());
            Vm.SetViewVisible(true);
            Vm.EnableLiveTrackingRequested += Requests.Add;
            Vm.EnableLiveTrackingCommand.CanExecuteChanged += (_, _) => CanExecuteChanges++;
            return this;
        }

        /// <summary>What M5.8.1 reports while the bridge is absent for an identified process.</summary>
        public void Disable(string kind = "identified", GameProcessInfo? process = null)
        {
            Service.CurrentIdentification = Identify(kind, process);
            Service.Set(MonitoringState.Connecting);
            Service.SetDiagnostic(MonitoringDiagnostic.LiveTrackingDisabled);
        }

        public IdentifiedGame Game => Service.CurrentIdentification!.Game!;

        public void Dispose() => Vm?.Dispose();
    }

    // ---------------- eligibility ----------------

    [Fact]
    public async Task The_action_is_offered_for_an_eligible_game_and_raises_a_request_for_its_process()
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable();

        Assert.True(rig.Vm.IsEnableLiveTrackingOffered);
        Assert.True(rig.Vm.CanEnableLiveTracking);
        Assert.True(rig.Vm.EnableLiveTrackingCommand.CanExecute(null));

        rig.Vm.EnableLiveTrackingCommand.Execute(null);

        var request = Assert.Single(rig.Requests);
        Assert.Equal(new ProcessFingerprint(100, Start), request.Process);
        Assert.Equal("zelda-ocarina-of-time", request.GameId.Value);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unsupported")]
    [InlineData("conflict")]
    public async Task The_action_is_hidden_for_unidentified_games(string kind)
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable(kind);

        Assert.True(rig.Vm.HasDiagnostic);
        Assert.False(rig.Vm.IsEnableLiveTrackingOffered);
        Assert.False(rig.Vm.EnableLiveTrackingCommand.CanExecute(null));
        rig.Vm.EnableLiveTrackingCommand.Execute(null);
        Assert.Empty(rig.Requests);
    }

    [Fact]
    public async Task The_action_is_hidden_without_identification_or_diagnostic()
    {
        using var rig = await new Rig().StartAsync();
        rig.Service.Set(MonitoringState.Connecting);
        rig.Service.SetDiagnostic(MonitoringDiagnostic.LiveTrackingDisabled); // identification unavailable
        Assert.False(rig.Vm.IsEnableLiveTrackingOffered);

        rig.Service.CurrentIdentification = Identify("identified");
        rig.Service.SetDiagnostic(MonitoringDiagnostic.None);
        Assert.False(rig.Vm.IsEnableLiveTrackingOffered);
    }

    [Theory]
    [InlineData(MonitoringState.Monitoring)]
    [InlineData(MonitoringState.Ambiguous)]
    [InlineData(MonitoringState.Searching)]
    [InlineData(MonitoringState.Reconnecting)]
    [InlineData(MonitoringState.Error)]
    public async Task The_action_is_hidden_in_other_states(MonitoringState state)
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable();
        rig.Service.Set(state);
        Assert.False(rig.Vm.IsEnableLiveTrackingOffered);
    }

    [Fact]
    public async Task The_action_is_disabled_while_an_attempt_runs()
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable();
        var changes = rig.CanExecuteChanges;

        rig.Vm.SetLiveTrackingSetupInProgress(true);

        Assert.True(rig.Vm.IsEnableLiveTrackingOffered); // still shown...
        Assert.False(rig.Vm.CanEnableLiveTracking);      // ...but disabled
        Assert.False(rig.Vm.EnableLiveTrackingCommand.CanExecute(null));
        Assert.True(rig.CanExecuteChanges > changes);
        rig.Vm.EnableLiveTrackingCommand.Execute(null);
        Assert.Empty(rig.Requests);

        rig.Vm.SetLiveTrackingSetupInProgress(false);
        Assert.True(rig.Vm.CanEnableLiveTracking);
    }

    // ---------------- results ----------------

    [Fact]
    public async Task Success_shows_restart_instructions_keeps_the_diagnostic_and_retires_the_action()
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable();

        rig.Vm.ShowLiveTrackingSetupResult(rig.Game, LiveTrackingSetupResult.Enabled);

        var expected = $"Live tracking is enabled for the next launch. Restart {OotName} to start tracking — the in-game Restart option also works.";
        Assert.Equal(expected, rig.Vm.LiveTrackingSetupMessage);
        Assert.Equal(expected, rig.Vm.DiagnosticHint);
        Assert.False(rig.Vm.IsEnableLiveTrackingOffered);

        // The running game still has no bridge: the diagnostic stays truthful.
        Assert.True(rig.Vm.HasDiagnostic);
        Assert.Equal("Live tracking disabled", rig.Vm.MonitoringStatusText);
        Assert.Equal("Live tracking disabled for this game", rig.Vm.StatusSummary);
    }

    [Fact]
    public async Task A_recovery_failure_shows_the_stronger_warning()
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable();

        rig.Vm.ShowLiveTrackingSetupResult(rig.Game, LiveTrackingSetupResult.RecoveryFailed);

        Assert.StartsWith("Warning:", rig.Vm.DiagnosticHint, StringComparison.Ordinal);
        Assert.Contains("check the end of the file before trying again", rig.Vm.DiagnosticHint, StringComparison.Ordinal);
        Assert.False(rig.Vm.IsEnableLiveTrackingOffered);
    }

    [Theory]
    [InlineData(LiveTrackingSetupResult.FileConflict, "already sets HOS_CONTROL to a different value")]
    [InlineData(LiveTrackingSetupResult.EnvironmentConflict, "takes priority over longshot_env.txt")]
    [InlineData(LiveTrackingSetupResult.AccessDenied, "denied access")]
    [InlineData(LiveTrackingSetupResult.FileInUse, "open in another program")]
    [InlineData(LiveTrackingSetupResult.WriteFailed, "left as it was")]
    [InlineData(LiveTrackingSetupResult.ProcessChanged, "The game changed")]
    public async Task Failures_explain_what_happened_and_allow_another_attempt(LiveTrackingSetupResult result, string fragment)
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable();

        rig.Vm.ShowLiveTrackingSetupResult(rig.Game, result);

        Assert.Contains(fragment, rig.Vm.DiagnosticHint, StringComparison.Ordinal);
        Assert.True(rig.Vm.IsEnableLiveTrackingOffered);
    }

    [Fact]
    public async Task Already_enabled_mentions_a_possible_override()
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable();

        rig.Vm.ShowLiveTrackingSetupResult(rig.Game, LiveTrackingSetupResult.AlreadyEnabled);

        Assert.Contains("another HOS_CONTROL setting may be taking priority", rig.Vm.DiagnosticHint, StringComparison.Ordinal);
        Assert.False(rig.Vm.IsEnableLiveTrackingOffered);
    }

    [Fact]
    public void Every_result_has_a_message_without_paths_except_cancellation()
    {
        foreach (var result in Enum.GetValues<LiveTrackingSetupResult>())
        {
            var message = DashboardFormat.LiveTrackingSetup(result, OotName);
            if (result == LiveTrackingSetupResult.Cancelled)
            {
                Assert.Null(message);
                continue;
            }
            Assert.False(string.IsNullOrWhiteSpace(message), result.ToString());
            Assert.DoesNotContain(@":\", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_result_for_another_process_is_never_shown()
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable();
        var other = Identify("identified", Process200).Game!;

        rig.Vm.ShowLiveTrackingSetupResult(other, LiveTrackingSetupResult.Enabled);

        Assert.Null(rig.Vm.LiveTrackingSetupMessage);
        Assert.Equal(DashboardFormat.LiveTrackingDisabledHint, rig.Vm.DiagnosticHint);
        Assert.True(rig.Vm.IsEnableLiveTrackingOffered);
    }

    [Fact]
    public async Task A_process_change_clears_the_result_and_offers_the_action_for_the_new_process()
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable();
        rig.Vm.ShowLiveTrackingSetupResult(rig.Game, LiveTrackingSetupResult.Enabled);

        rig.Disable(process: Process200);

        Assert.Null(rig.Vm.LiveTrackingSetupMessage);
        Assert.True(rig.Vm.IsEnableLiveTrackingOffered);
        rig.Vm.EnableLiveTrackingCommand.Execute(null);
        Assert.Equal(200, Assert.Single(rig.Requests).Process.ProcessId);
    }

    [Fact]
    public async Task The_result_clears_with_the_diagnostic()
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable();
        rig.Vm.ShowLiveTrackingSetupResult(rig.Game, LiveTrackingSetupResult.Enabled);

        rig.Service.SetDiagnostic(MonitoringDiagnostic.None);

        Assert.Null(rig.Vm.LiveTrackingSetupMessage);
        Assert.Null(rig.Vm.DiagnosticHint);
        Assert.False(rig.Vm.IsEnableLiveTrackingOffered);
    }

    [Fact]
    public async Task Service_replacement_and_stopping_leave_nothing_actionable()
    {
        using var rig = await new Rig().StartAsync();
        rig.Disable();
        rig.Vm.ShowLiveTrackingSetupResult(rig.Game, LiveTrackingSetupResult.FileConflict);

        Assert.True(await rig.Host.RestartAsync());
        Assert.False(rig.Vm.IsEnableLiveTrackingOffered);
        Assert.Null(rig.Vm.LiveTrackingSetupMessage);

        rig.Disable();
        Assert.True(rig.Vm.IsEnableLiveTrackingOffered); // a fresh offer, no stale result
        Assert.Null(rig.Vm.LiveTrackingSetupMessage);

        Assert.True(await rig.Host.StopAsync());
        Assert.False(rig.Vm.IsEnableLiveTrackingOffered);
    }

    [Fact]
    public async Task Disposal_stops_all_requests()
    {
        var rig = await new Rig().StartAsync();
        rig.Disable();

        rig.Vm.Dispose();
        rig.Vm.EnableLiveTrackingCommand.Execute(null);
        rig.Vm.ShowLiveTrackingSetupResult(rig.Game, LiveTrackingSetupResult.Enabled);

        Assert.Empty(rig.Requests);
    }

    // ---------------- window ----------------

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument MainWindowXaml([CallerFilePath] string thisFile = "")
    {
        // Located from this source file, so it works whatever folder the tests are built into.
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SwitchAchievements.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir.FullName, "src", "SwitchAchievements.App", "MainWindow.xaml"));
    }

    [Fact]
    public void The_button_is_bound_to_the_action_and_uses_the_existing_style()
    {
        var doc = MainWindowXaml();
        var button = Assert.Single(doc.Descendants(Presentation + "Button"),
            b => (string?)b.Attribute("Command") == "{Binding Dashboard.EnableLiveTrackingCommand}");

        Assert.Equal("Enable live tracking", (string?)button.Attribute("AutomationProperties.Name"));
        var style = Assert.Single(button.Descendants(Presentation + "Style"));
        Assert.Equal("{StaticResource MetroGlossButton}", (string?)style.Attribute("BasedOn"));
        Assert.Contains(style.Descendants(Presentation + "DataTrigger"),
            t => (string?)t.Attribute("Binding") == "{Binding Dashboard.IsEnableLiveTrackingOffered}" && (string?)t.Attribute("Value") == "True");
        Assert.Contains(button.Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute("Text") == DashboardFormat.EnableLiveTrackingAction);

        // Beside the M5.8.1 hint row, inside the same freshness strip.
        var hint = doc.Descendants(Presentation + "TextBlock")
            .Single(t => (string?)t.Attribute("Text") == "{Binding Dashboard.DiagnosticHint}");
        Assert.Same(hint.Parent!.Parent, button.Parent);
    }
}
