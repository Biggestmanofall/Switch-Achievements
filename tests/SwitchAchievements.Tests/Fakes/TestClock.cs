namespace SwitchAchievements.Tests.Fakes;

/// <summary>
/// A settable "now" that is safe to change from the test thread while a monitoring loop
/// reads it (M5.8.1). Only <see cref="GetUtcNow"/> is overridden: waits and timers keep
/// using real time, as the monitoring service's poll-interval delays do.
/// </summary>
internal sealed class TestClock : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset now) => _now = now;

    public DateTimeOffset Now
    {
        get { lock (_lock) return _now; }
        set { lock (_lock) _now = value; }
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock) _now += by;
    }

    public override DateTimeOffset GetUtcNow() => Now;
}
