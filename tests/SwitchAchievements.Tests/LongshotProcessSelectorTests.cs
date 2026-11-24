using SwitchAchievements.Core;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.Tests;

public class LongshotProcessSelectorTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string GamePathA = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private const string GamePathB = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private const string StubPath = @"C:\Recomp Library\longshot\Longshot\longshot.exe";

    private static GameProcessInfo Proc(int pid, string path, DateTime? start = null, int? parent = null)
        => new(pid, path, start ?? Start) { ParentProcessId = parent };

    [Theory]
    [InlineData(@"C:\Recomp Library\longshot\Longshot\bin\longshot.exe", true)]
    [InlineData(@"C:\Recomp Library\longshot\Longshot\longshot.exe", false)]
    [InlineData(@"C:\Recomp Library\longshot\Longshot\bin\other.exe", false)]
    [InlineData("", false)]
    public void IsGameBinaryPath_classifies_by_bin_folder(string path, bool expected)
        => Assert.Equal(expected, LongshotProcessSelector.IsGameBinaryPath(path));

    [Fact]
    public void No_game_candidates_reports_none()
    {
        var result = LongshotProcessSelector.Select(new[] { Proc(1, StubPath) }, bridge: null);

        Assert.Equal(ProcessSelectionStatus.None, result.Selection);
        Assert.Null(result.SelectedProcess);
    }

    [Fact]
    public void Single_game_candidate_is_unique()
    {
        var result = LongshotProcessSelector.Select(new[] { Proc(10, GamePathA) }, bridge: null);

        Assert.Equal(ProcessSelectionStatus.Unique, result.Selection);
        Assert.Equal(10, result.SelectedProcess!.ProcessId);
    }

    [Fact]
    public void Single_game_candidate_with_responding_bridge_is_still_unique()
    {
        var bridge = new BridgeProbeResult(true, true, null, null);
        var result = LongshotProcessSelector.Select(new[] { Proc(10, GamePathA) }, bridge);

        Assert.Equal(ProcessSelectionStatus.Unique, result.Selection);
        Assert.Equal(BridgeReadinessStatus.Responding, result.Readiness);
        Assert.False(result.OwnerVerified);
    }

    [Fact]
    public void Two_candidates_without_ownership_are_ambiguous_even_if_bridge_responds()
    {
        var bridge = new BridgeProbeResult(true, true, null, null);
        var result = LongshotProcessSelector.Select(new[] { Proc(10, GamePathA), Proc(20, GamePathB) }, bridge);

        Assert.Equal(ProcessSelectionStatus.Ambiguous, result.Selection);
        Assert.Equal(BridgeReadinessStatus.Responding, result.Readiness);
        Assert.Null(result.SelectedProcess);
    }

    [Fact]
    public void Two_candidates_with_unavailable_bridge_are_ambiguous()
    {
        var bridge = new BridgeProbeResult(false, false, null, "refused");
        var result = LongshotProcessSelector.Select(new[] { Proc(10, GamePathA), Proc(20, GamePathB) }, bridge);

        Assert.Equal(ProcessSelectionStatus.Ambiguous, result.Selection);
        Assert.Equal(BridgeReadinessStatus.Unavailable, result.Readiness);
    }

    [Fact]
    public void Verified_owner_resolves_ambiguity()
    {
        var result = LongshotProcessSelector.Select(
            new[] { Proc(10, GamePathA), Proc(20, GamePathB) },
            bridge: null,
            verifiedOwnership: new VerifiedOwnership(20));

        Assert.Equal(ProcessSelectionStatus.Unique, result.Selection);
        Assert.Equal(20, result.SelectedProcess!.ProcessId);
        Assert.True(result.OwnerVerified);
    }

    [Fact]
    public void Verified_owner_mismatch_is_ambiguous()
    {
        var result = LongshotProcessSelector.Select(
            new[] { Proc(10, GamePathA), Proc(20, GamePathB) },
            bridge: null,
            verifiedOwnership: new VerifiedOwnership(999));

        Assert.Equal(ProcessSelectionStatus.Ambiguous, result.Selection);
        Assert.Null(result.SelectedProcess);
        Assert.False(result.OwnerVerified);
    }

    [Fact]
    public void Experimental_server_pid_does_not_resolve_ambiguity()
    {
        var bridge = new BridgeProbeResult(true, true, (uint)20, null);
        var result = LongshotProcessSelector.Select(new[] { Proc(10, GamePathA), Proc(20, GamePathB) }, bridge);

        Assert.Equal(ProcessSelectionStatus.Ambiguous, result.Selection);
        Assert.Null(result.SelectedProcess);
    }

    [Fact]
    public void Parent_child_and_start_time_do_not_auto_select()
    {
        var parentGame = Proc(10, GamePathA, parent: 5);
        var childGame = Proc(20, GamePathB, start: new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc), parent: 10);

        var result = LongshotProcessSelector.Select(new[] { parentGame, childGame }, bridge: null);

        Assert.Equal(ProcessSelectionStatus.Ambiguous, result.Selection);
        Assert.Null(result.SelectedProcess);
    }

    [Fact]
    public void Inaccessible_path_is_not_classified_and_not_silently_selected()
    {
        var unknown = Proc(30, string.Empty);
        var known = Proc(10, GamePathA);

        var result = LongshotProcessSelector.Select(new[] { unknown, known }, bridge: null);

        Assert.Equal(ProcessSelectionStatus.Unique, result.Selection);
        Assert.Equal(10, result.SelectedProcess!.ProcessId);
        Assert.Contains(result.Diagnostics, d => d.Contains("path=<unavailable>"));
    }

    [Fact]
    public void Core_selector_obeys_the_selection_truth_table()
    {
        Assert.Equal(
            ProcessSelectionStatus.None,
            ProcessSelector.Select(Array.Empty<GameProcessInfo>(), bridge: null).Selection);

        Assert.Equal(
            ProcessSelectionStatus.Unique,
            ProcessSelector.Select(new[] { Proc(10, GamePathA) }, bridge: null).Selection);

        Assert.Equal(
            ProcessSelectionStatus.Ambiguous,
            ProcessSelector.Select(new[] { Proc(10, GamePathA), Proc(20, GamePathB) }, bridge: null).Selection);

        var owner = ProcessSelector.Select(
            new[] { Proc(10, GamePathA), Proc(20, GamePathB) }, bridge: null, verifiedOwnership: new VerifiedOwnership(20));

        Assert.Equal(ProcessSelectionStatus.Unique, owner.Selection);
        Assert.True(owner.OwnerVerified);
    }
}