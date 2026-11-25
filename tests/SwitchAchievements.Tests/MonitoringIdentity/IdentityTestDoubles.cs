using System.Collections.Concurrent;
using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Identity;

namespace SwitchAchievements.Tests.MonitoringIdentity;

/// <summary>
/// Scriptable <see cref="IGameIdentityProvider"/> for lifecycle tests: records every
/// call, can block its first call on a gate (honouring cancellation), or throw.
/// </summary>
internal sealed class FakeIdentityProvider : IGameIdentityProvider
{
    private readonly object _lock = new();
    private readonly List<GameProcessInfo> _calls = new();

    public Func<GameProcessInfo, GameIdentityEvidence> Respond { get; set; } = IdentityEvidence.Oot;

    /// <summary>When set, the first call waits for this gate (or for cancellation) before responding.</summary>
    public TaskCompletionSource? FirstCallGate { get; set; }

    public Exception? Throw { get; set; }

    public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool ObservedCancellation { get; private set; }

    public IReadOnlyList<GameProcessInfo> Calls
    {
        get { lock (_lock) return _calls.ToArray(); }
    }

    public async Task<GameIdentityEvidence> CollectAsync(GameProcessInfo selected, CancellationToken ct)
    {
        int call;
        lock (_lock)
        {
            _calls.Add(selected);
            call = _calls.Count;
        }

        if (call == 1)
            FirstCallStarted.TrySetResult();

        if (Throw is not null)
            throw Throw;

        if (call == 1 && FirstCallGate is { } gate)
        {
            try
            {
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
        }

        return Respond(selected);
    }
}

/// <summary>Evidence builders bound to a selected process (Longshot keys, canonical values).</summary>
internal static class IdentityEvidence
{
    public const string OtherBuild = "1111111111111111111111111111111111111111";

    public static GameIdentityEvidence Oot(GameProcessInfo p) => Make(p, IdentityTestData.OotFacts());

    public static GameIdentityEvidence UnsupportedBuild(GameProcessInfo p) => Make(p, IdentityTestData.OotFacts(build: OtherBuild));

    public static GameIdentityEvidence NoFacts(GameProcessInfo p) => Make(p, IdentityTestData.Facts());

    public static GameIdentityEvidence Conflicting(GameProcessInfo p)
        => Make(p, IdentityTestData.OotFacts(), new EvidenceIssue(EvidenceIssueKind.Conflict, "sources disagree", IdentityTestData.ProgramKey));

    public static GameIdentityEvidence For(string kind, GameProcessInfo p) => kind switch
    {
        "identified" => Oot(p),
        "unknown" => NoFacts(p),
        "unsupported" => UnsupportedBuild(p),
        "conflict" => Conflicting(p),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static GameIdentityEvidence Make(GameProcessInfo p, IReadOnlyDictionary<string, string> facts, params EvidenceIssue[] issues)
        => new(IdentityTestData.LongshotIntegration, new ProcessFingerprint(p.ProcessId, p.ProcessStartUtc), facts, issues,
            new DateTime(2026, 11, 24, 6, 0, 0, DateTimeKind.Utc));
}

/// <summary>Thread-safe in-memory logger.</summary>
internal sealed class CapturingLogger : IAppLogger
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public int Count(string fragment) => Lines.Count(l => l.Contains(fragment, StringComparison.Ordinal));

    public void Info(string message) => Lines.Enqueue("INFO " + message);
    public void Warn(string message) => Lines.Enqueue("WARN " + message);
    public void Error(string message) => Lines.Enqueue("ERROR " + message);
    public void Dispose() { }
}
