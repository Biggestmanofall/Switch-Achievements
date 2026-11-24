namespace SwitchAchievements.Core;

/// <summary>
/// Pure, integration-neutral selection policy. Callers pass an already-filtered
/// list of game candidates; this type applies the none/unique/ambiguous rules and
/// the verified-ownership resolution without any Windows or Longshot knowledge.
///
/// Rule of thumb: a second candidate is only ever resolved by an independently
/// verified owner (<see cref="VerifiedOwnership"/>), never by "most likely is the
/// parent", "newest", or the first element in the list.
/// </summary>
public static class ProcessSelector
{
    public static ProcessSelectionResult Select(
        IReadOnlyList<GameProcessInfo> candidates,
        BridgeProbeResult? bridge,
        VerifiedOwnership? verifiedOwnership = null)
    {
        var diagnostics = new List<string>();

        BridgeReadinessStatus readiness;
        if (bridge is null || !bridge.Connected)
            readiness = BridgeReadinessStatus.Unavailable;
        else if (bridge.Responding)
            readiness = BridgeReadinessStatus.Responding;
        else
            readiness = BridgeReadinessStatus.Connected;

        if (candidates.Count == 0)
        {
            diagnostics.Add("no game candidates");
            return new ProcessSelectionResult(ProcessSelectionStatus.None, readiness, null, diagnostics);
        }

        if (candidates.Count == 1)
        {
            return new ProcessSelectionResult(ProcessSelectionStatus.Unique, readiness, candidates[0], diagnostics);
        }

        // Multiple candidates.
        if (verifiedOwnership is not null)
        {
            var matches = candidates
                .Where(c => c.ProcessId == verifiedOwnership.ServerProcessId)
                .ToList();

            if (matches.Count == 1)
            {
                diagnostics.Add($"verified owner PID {verifiedOwnership.ServerProcessId} matched one candidate");
                return new ProcessSelectionResult(
                    ProcessSelectionStatus.Unique, readiness, matches[0], diagnostics, OwnerVerified: true);
            }

            diagnostics.Add($"verified owner PID {verifiedOwnership.ServerProcessId} matched {matches.Count} candidates; refusing to guess");
            return new ProcessSelectionResult(ProcessSelectionStatus.Ambiguous, readiness, null, diagnostics);
        }

        diagnostics.Add($"{candidates.Count} candidates and no verified ownership; ambiguous");
        return new ProcessSelectionResult(ProcessSelectionStatus.Ambiguous, readiness, null, diagnostics);
    }
}