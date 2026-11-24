namespace SwitchAchievements.Core;

/// <summary>
/// Outcome of a read-only probe of the control bridge. <see cref="ExperimentalServerProcessId"/>
/// is surfaced for diagnostics only and must never be used to resolve process
/// selection on its own.
/// </summary>
public sealed record BridgeProbeResult(
    bool Connected,
    bool Responding,
    uint? ExperimentalServerProcessId,
    string? Error);

/// <summary>
/// A read-only probe of a named-pipe control bridge. Implementations connect and
/// (optionally) issue a read-only status request, and may report an experimental
/// server-process identifier from the same connection.
/// </summary>
public interface INamedPipeProbe
{
    Task<BridgeProbeResult> ProbeAsync(string pipeName, CancellationToken ct = default);
}