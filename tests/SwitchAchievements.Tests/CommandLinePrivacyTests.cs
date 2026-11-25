using System.Collections.Concurrent;
using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;
using SwitchAchievements.Tests.Fakes;
using static SwitchAchievements.Tests.Fakes.LongshotCommandLines;

namespace SwitchAchievements.Tests;

/// <summary>
/// M5.6 R1 privacy: command lines are read only to classify helpers and must
/// never reach logs, status messages, diagnostics, or persisted process models.
/// </summary>
public class CommandLinePrivacyTests
{
    // A recognisable fragment standing in for private data (user name, paths).
    private const string Secret = "PRIVATE-USER-7f3a9c";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string SecretGame() => $"\"C:\\Users\\{Secret}\\Longshot\\bin\\longshot.exe\" \"--{Secret}\"";

    private static string SecretWatch(int gamePid)
        => $"\"C:\\Users\\{Secret}\\Longshot\\bin\\longshot.exe\" \"--crash-watch\" \"{gamePid}\" \"{RunId}\" "
         + $"\"C:\\Users\\{Secret}\\Longshot\" \"C:\\Users\\{Secret}\\log.txt\" \"C:\\Users\\{Secret}\\opts.ini\"";

    private static string SecretMalformed() => $"\"C:\\Users\\{Secret}\\x.exe\" \"--crash-watch\" \"{Secret}\"";

    private static FakeCommandLineReader SecretReader()
        => new FakeCommandLineReader()
            .Set(100, SecretGame())
            .Set(200, SecretWatch(100))
            .Set(300, SecretMalformed());

    private static GameProcessInfo[] Processes() => new[]
    {
        new GameProcessInfo(100, Exe, T0),
        new GameProcessInfo(200, Exe, T0.AddMilliseconds(30)),
        new GameProcessInfo(300, Exe, T0.AddMilliseconds(60)),
        new GameProcessInfo(400, Exe, T0.AddMilliseconds(90)), // unreadable
    };

    private sealed class CapturingLogger : IAppLogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public void Info(string message) => Lines.Enqueue(message);
        public void Warn(string message) => Lines.Enqueue(message);
        public void Error(string message) => Lines.Enqueue(message);
        public void Dispose() { }
    }

    [Fact]
    public void Integration_status_messages_and_candidates_never_contain_command_lines()
    {
        var integration = new LongshotGameIntegration("unused_" + Guid.NewGuid().ToString("N"), SecretReader());
        var messages = new List<string>();
        integration.StatusChanged += messages.Add;

        var candidates = integration.SelectCandidates(Processes());

        Assert.Contains(messages, m => m.StartsWith("process classification:", StringComparison.Ordinal));
        Assert.All(messages, m => Assert.DoesNotContain(Secret, m));
        Assert.All(candidates, c => Assert.Null(c.CommandLine));
        Assert.DoesNotContain(candidates, c => c.ProcessId == 200); // helper excluded
    }

    [Fact]
    public void Classification_results_and_diagnostics_never_contain_command_lines()
    {
        var selection = new LongshotProcessRoleClassifier(SecretReader()).SelectCandidates(Processes());

        Assert.All(selection.Diagnostics, d => Assert.DoesNotContain(Secret, d));
        Assert.All(selection.Classifications, c => Assert.DoesNotContain(Secret, c.ToString()));
        Assert.All(selection.Candidates, c => Assert.Null(c.CommandLine));
    }

    [Fact]
    public void Poc_selector_diagnostics_never_contain_command_lines()
    {
        var result = LongshotProcessSelector.Select(Processes(), bridge: null, verifiedOwnership: null,
            classifier: new LongshotProcessRoleClassifier(SecretReader()));

        Assert.All(result.Diagnostics, d => Assert.DoesNotContain(Secret, d));
        Assert.Null(result.SelectedProcess?.CommandLine);
    }

    [Fact]
    public async Task Monitoring_log_never_contains_command_lines()
    {
        var logger = new CapturingLogger();
        // Unique pipe name: no real bridge exists, so connects simply fail/time out.
        var integration = new LongshotGameIntegration("privacy_" + Guid.NewGuid().ToString("N"), SecretReader());
        var provider = new FakeProcessProvider { Processes = Processes() };
        var service = new MonitoringService(integration, provider, logger,
            requiredStableObservations: 2, pollInterval: TimeSpan.FromMilliseconds(20));

        await service.StartAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !logger.Lines.Any(l => l.StartsWith("process classification:", StringComparison.Ordinal)))
            await Task.Delay(10);
        await service.StopAsync();

        Assert.Contains(logger.Lines, l => l.StartsWith("process classification:", StringComparison.Ordinal));
        Assert.All(logger.Lines, l => Assert.DoesNotContain(Secret, l));
        Assert.Null(service.Session.Latest);
    }

    [Fact]
    public void Process_provider_model_keeps_command_line_null()
    {
        // The real provider must keep the M3 rule: command lines are never collected into the model.
        // Enumerate this test process's own name so at least one known process is always checked.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var processes = new WindowsProcessProvider().GetProcesses(self.ProcessName);

        var own = Assert.Single(processes, p => p.ProcessId == self.Id);
        Assert.Null(own.CommandLine);
        Assert.All(processes, p => Assert.Null(p.CommandLine));
    }
}
