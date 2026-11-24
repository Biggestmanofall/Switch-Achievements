namespace SwitchAchievements.Core;

/// <summary>Process-selection outcome, independent of bridge readiness.</summary>
public enum ProcessSelectionStatus
{
    /// <summary>No matching game process was found.</summary>
    None,

    /// <summary>Exactly one game process (or an independently verified owner among several).</summary>
    Unique,

    /// <summary>Multiple candidates whose owner could not be reliably established.</summary>
    Ambiguous,
}

/// <summary>Whether the control bridge could be reached and would answer a read-only status.</summary>
public enum BridgeReadinessStatus
{
    /// <summary>Could not connect to the pipe.</summary>
    Unavailable,

    /// <summary>Connected, but a read-only status request did not succeed.</summary>
    Connected,

    /// <summary>Connected and a read-only status request succeeded (ok:true).</summary>
    Responding,
}

/// <summary>
/// Independently verified evidence of which process hosts the named-pipe server.
/// This is only ever produced by a cross-checked observation (for example a
/// Sysinternals Handle/Process Explorer capture), never by the experimental
/// client-handle API alone.
/// </summary>
public sealed record VerifiedOwnership(int ServerProcessId);

/// <summary>
/// The combined result of process selection plus bridge readiness. Selection and
/// readiness are deliberately kept as two independent facts: a single candidate
/// may be <see cref="ProcessSelectionStatus.Unique"/> while the bridge is still
/// <see cref="BridgeReadinessStatus.Unavailable"/>, and multiple candidates stay
/// <see cref="ProcessSelectionStatus.Ambiguous"/> even when the bridge responds.
/// </summary>
public sealed record ProcessSelectionResult(
    ProcessSelectionStatus Selection,
    BridgeReadinessStatus Readiness,
    GameProcessInfo? SelectedProcess,
    IReadOnlyList<string> Diagnostics,
    bool OwnerVerified = false);