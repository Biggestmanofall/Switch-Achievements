using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;
using SwitchAchievements.Tests.Fakes;
using static SwitchAchievements.Tests.Fakes.LongshotCommandLines;

namespace SwitchAchievements.Tests;

/// <summary>
/// M5.6 R1: the monitoring pipeline with Longshot crash-reporter helper
/// classification (real classifier, fake processes and command lines).
/// </summary>
public class MonitoringServiceHelperClassificationTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static GameProcessInfo Proc(int pid, DateTime start) => new(pid, Exe, start);

    private static (FakeGameIntegration Integration, FakeProcessProvider Provider, FakeCommandLineReader Reader, MonitoringService Service) Create()
    {
        var reader = new FakeCommandLineReader();
        var classifier = new LongshotProcessRoleClassifier(reader);
        var integration = new FakeGameIntegration
        {
            SelectCandidatesOverride = processes =>
                classifier.SelectCandidates(processes.Where(p => LongshotProcessSelector.IsGameBinaryPath(p.ExecutablePath)).ToList()).Candidates,
        };
        var provider = new FakeProcessProvider();
        var service = new MonitoringService(integration, provider, NullLogger.Instance,
            requiredStableObservations: 2, pollInterval: TimeSpan.FromMilliseconds(20));
        return (integration, provider, reader, service);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for " + what);
    }

    private static Task WaitForStateAsync(MonitoringService service, MonitoringState state)
        => WaitForAsync(() => service.State == state, $"{state} (actual {service.State})");

    private static void AssertNeverConnectedTo(FakeGameIntegration integration, params int[] helperPids)
    {
        lock (integration.ConnectedProcesses)
        {
            Assert.DoesNotContain(integration.ConnectedProcesses, p => helperPids.Contains(p.ProcessId));
        }
    }

    [Fact]
    public async Task Game_plus_crash_watch_helper_connects_to_the_game()
    {
        var (integration, provider, reader, service) = Create();
        reader.Set(100, Game()).Set(200, CrashWatch(100));
        provider.Processes = new[] { Proc(100, T0), Proc(200, T0.AddMilliseconds(30)) };

        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        Assert.Equal(100, integration.LastConnectedProcess!.ProcessId);
        AssertNeverConnectedTo(integration, 200);
        await service.StopAsync();
    }

    [Fact]
    public async Task Helper_only_yields_no_candidate_and_never_connects()
    {
        var (integration, provider, reader, service) = Create();
        reader.Set(200, CrashWatch(100)); // orphaned: the game has exited
        provider.Processes = new[] { Proc(200, T0) };

        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Searching);
        await WaitForAsync(() => integration.SelectCandidatesCount >= 4, "several discovery polls");

        Assert.Equal(MonitoringState.Searching, service.State);
        Assert.Equal(0, integration.ConnectCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task Game_plus_unreadable_candidate_stays_ambiguous()
    {
        var (integration, provider, reader, service) = Create();
        reader.Set(100, Game()); // pid 300: no entry → unreadable
        provider.Processes = new[] { Proc(100, T0), Proc(300, T0.AddMilliseconds(30)) };

        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Ambiguous);
        await WaitForAsync(() => integration.SelectCandidatesCount >= 4, "several discovery polls");

        Assert.Equal(MonitoringState.Ambiguous, service.State);
        Assert.Equal(0, integration.ConnectCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task Game_plus_throwing_reader_candidate_stays_ambiguous()
    {
        var (integration, provider, reader, service) = Create();
        reader.Set(100, Game()).Throw(300);
        provider.Processes = new[] { Proc(100, T0), Proc(300, T0.AddMilliseconds(30)) };

        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Ambiguous);
        Assert.Equal(0, integration.ConnectCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task Two_genuine_games_plus_two_helpers_stay_ambiguous()
    {
        var (integration, provider, reader, service) = Create();
        reader.Set(100, Game()).Set(101, CrashWatch(100))
              .Set(300, Game()).Set(301, CrashWatch(300));
        provider.Processes = new[]
        {
            Proc(100, T0), Proc(101, T0.AddMilliseconds(30)),
            Proc(300, T0.AddSeconds(5)), Proc(301, T0.AddSeconds(5).AddMilliseconds(30)),
        };

        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Ambiguous);
        await WaitForAsync(() => integration.SelectCandidatesCount >= 4, "several discovery polls");

        Assert.Equal(MonitoringState.Ambiguous, service.State);
        Assert.Equal(0, integration.ConnectCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task Game_plus_crash_show_helper_connects_to_the_game()
    {
        var (integration, provider, reader, service) = Create();
        reader.Set(100, Game()).Set(150, CrashShow());
        provider.Processes = new[] { Proc(100, T0), Proc(150, T0.AddMilliseconds(30)) };

        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        Assert.Equal(100, integration.LastConnectedProcess!.ProcessId);
        AssertNeverConnectedTo(integration, 150);
        await service.StopAsync();
    }

    [Fact]
    public async Task Restart_sequence_gives_the_new_game_a_new_session()
    {
        // Old game A (100) + helper Ha (101); Restart Now starts B (200) as A's
        // child with the same (game) command line; A exits; B starts helper Hb (201).
        var (integration, provider, reader, service) = Create();
        reader.Set(100, Game()).Set(101, CrashWatch(100))
              .Set(200, Game()).Set(201, CrashWatch(200));

        var a = Proc(100, T0);
        var ha = Proc(101, T0.AddMilliseconds(30));
        var b = Proc(200, T0.AddMinutes(5)) with { ParentProcessId = 100 };
        var hb = Proc(201, T0.AddMinutes(5).AddMilliseconds(30)) with { ParentProcessId = 200 };

        // Phase 1: old game + helper.
        provider.Processes = new[] { a, ha };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);
        Assert.Equal(100, integration.LastConnectedProcess!.ProcessId);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, a.ProcessStartUtc), rupees: 1));
        await WaitForAsync(() => service.Session.Id is not null, "session for A");
        var sessionA = service.Session.Id;

        // Phase 2: overlap — the bridge drops, both games present → ambiguous, no connect.
        provider.Processes = new[] { a, ha, b };
        integration.RaiseDisconnected();
        await WaitForStateAsync(service, MonitoringState.Ambiguous);
        Assert.Equal(1, integration.ConnectCount);

        // Phase 3: old game gone (its helper may linger), new game + its helper.
        provider.Processes = new[] { ha, b, hb };
        await WaitForStateAsync(service, MonitoringState.Monitoring);
        Assert.Equal(200, integration.LastConnectedProcess!.ProcessId);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(200, b.ProcessStartUtc), rupees: 1));
        await WaitForAsync(() => service.Session.Latest?.SourceProcessId == 200, "session for B");

        Assert.NotNull(service.Session.Id);
        Assert.NotEqual(sessionA, service.Session.Id);
        AssertNeverConnectedTo(integration, 101, 201);
        await service.StopAsync();
    }

    [Fact]
    public async Task Revalidation_applies_the_same_classification_as_discovery()
    {
        // Every enumeration (discovery and the pre-connect revalidation) returns
        // game + helper. Without classification in revalidation the second
        // candidate would make it ambiguous and the connect would be abandoned.
        var (integration, provider, reader, service) = Create();
        reader.Set(100, Game()).Set(200, CrashWatch(100));
        var enumerations = 0;
        provider.OnGetProcesses = _ =>
        {
            Interlocked.Increment(ref enumerations);
            return new[] { Proc(100, T0), Proc(200, T0.AddMilliseconds(30)) };
        };

        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        Assert.Equal(1, integration.ConnectCount);
        Assert.Equal(100, integration.LastConnectedProcess!.ProcessId);
        // Every enumeration went through the integration's candidate filter.
        Assert.Equal(Volatile.Read(ref enumerations), integration.SelectCandidatesCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task Revalidation_never_selects_a_helper_left_after_the_game_exits()
    {
        // Discovery sees game + helper (→ the game); by revalidation the game has
        // exited and only its helper remains. The helper must not be connected.
        var (integration, provider, reader, service) = Create();
        reader.Set(100, Game()).Set(200, CrashWatch(100));
        var calls = 0;
        provider.OnGetProcesses = _ =>
        {
            var n = Interlocked.Increment(ref calls);
            return n <= 2
                ? new[] { Proc(100, T0), Proc(200, T0.AddMilliseconds(30)) }
                : new[] { Proc(200, T0.AddMilliseconds(30)) };
        };

        await service.StartAsync();
        await WaitForAsync(() => Volatile.Read(ref calls) >= 6, "discovery after revalidation");

        Assert.Equal(0, integration.ConnectCount);
        Assert.NotEqual(MonitoringState.Monitoring, service.State);
        await service.StopAsync();
    }
}
