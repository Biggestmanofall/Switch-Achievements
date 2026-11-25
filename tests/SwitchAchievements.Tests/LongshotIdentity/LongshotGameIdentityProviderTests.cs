using SwitchAchievements.App.Catalog;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;
using SwitchAchievements.Tests.Identity;
using static SwitchAchievements.Tests.LongshotIdentity.ExefsBytes;

namespace SwitchAchievements.Tests.LongshotIdentity;

/// <summary>
/// M5.6.4: the Longshot identity provider end to end — temporary installations,
/// scripted file-system failures, deterministic deadline, and resolver behaviour.
/// No real Longshot installation is used.
/// </summary>
public class LongshotGameIdentityProviderTests
{
    private const string ProgramKey = LongshotIdentityFormat.ProgramIdKey;
    private const string BuildKey = LongshotIdentityFormat.MainBuildIdKey;
    private static readonly DateTime StartUtc = new(2026, 11, 24, 5, 51, 31, 839, DateTimeKind.Utc);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private static GameProcessInfo Selected(string exePath, int pid = 34236, DateTime? start = null)
        => new(pid, exePath, start ?? StartUtc);

    private static LongshotGameIdentityProvider RealProvider(ManualTimeProvider? time = null)
        => new(LongshotFileAccess.Instance, time ?? new ManualTimeProvider(), LongshotGameIdentityProvider.DefaultTimeout);

    private static LongshotGameIdentityProvider ScriptedProvider(ScriptedFileAccess files, ManualTimeProvider? time = null)
        => new(files, time ?? new ManualTimeProvider(), LongshotGameIdentityProvider.DefaultTimeout);

    private static Task<GameIdentityEvidence> Collect(TempGame game, ManualTimeProvider? time = null)
        => RealProvider(time).CollectAsync(Selected(game.ExecutablePath), CancellationToken.None);

    private static EvidenceIssue AssertIssue(GameIdentityEvidence evidence, EvidenceIssueKind kind, string fragment, string? factKey = null)
    {
        var issue = evidence.Issues.FirstOrDefault(i => i.Kind == kind && i.Message.Contains(fragment, StringComparison.Ordinal));
        Assert.True(issue is not null,
            $"expected a {kind} issue containing \"{fragment}\"; got:\n{string.Join("\n", evidence.Issues.Select(i => $"{i.Kind} [{i.FactKey}] {i.Message}"))}");
        if (factKey is not null)
            Assert.Equal(factKey, issue!.FactKey);
        return issue!;
    }

    private static GameCatalog EmbeddedCatalog()
        => CatalogLoader.LoadEmbedded(new IIntegrationIdentityFormat[] { new LongshotIdentityFormat() }).Catalog!;

    // ---------------- valid identity ----------------

    [Fact]
    public void Provider_implements_the_core_contract()
        => Assert.IsAssignableFrom<IGameIdentityProvider>(new LongshotGameIdentityProvider());

    [Fact]
    public async Task Valid_installation_yields_both_facts_bound_to_the_selected_process()
    {
        using var game = TempGame.Valid();
        var time = new ManualTimeProvider();

        var evidence = await Collect(game, time);

        Assert.Equal("longshot", evidence.IntegrationId);
        Assert.Equal(LongshotIdentityFormat.Integration, evidence.IntegrationId);
        Assert.Equal(new ProcessFingerprint(34236, StartUtc), evidence.Process);
        Assert.Equal(2, evidence.Facts.Count);
        Assert.Equal(OotProgramIdHex, evidence.Facts[ProgramKey]);
        Assert.Equal(OotBuildIdHex, evidence.Facts[BuildKey]);
        Assert.Empty(evidence.Issues);
        Assert.Equal(time.UtcNow.UtcDateTime, evidence.CollectedUtc);
        Assert.Equal(new HashSet<string> { ProgramKey, BuildKey }, evidence.Facts.Keys.ToHashSet());
    }

    [Fact]
    public async Task Valid_evidence_is_identified_by_the_resolver_with_the_in_memory_catalog()
    {
        using var game = TempGame.Valid();
        var result = GameIdentityResolver.Resolve(IdentityTestData.OotCatalog(), await Collect(game));
        Assert.Equal(IdentificationStatus.Identified, result.Status);
        Assert.Equal("zelda-ocarina-of-time", result.Game!.GameId.Value);
        Assert.Equal(new ProcessFingerprint(34236, StartUtc), result.Game.Process);
    }

    [Fact]
    public async Task Valid_evidence_is_identified_by_the_resolver_with_the_embedded_catalog()
    {
        using var game = TempGame.Valid();
        var result = GameIdentityResolver.Resolve(EmbeddedCatalog(), await Collect(game));
        Assert.Equal(IdentificationStatus.Identified, result.Status);
        Assert.Equal("zelda-ocarina-of-time", result.Game!.GameId.Value);
    }

    // ---------------- partial evidence and resolver outcomes ----------------

    [Fact]
    public async Task Program_id_without_build_resolves_to_unknown()
    {
        using var game = new TempGame(Npdm(), nso: null);
        var evidence = await Collect(game);

        Assert.Equal(OotProgramIdHex, Assert.Single(evidence.Facts).Value);
        AssertIssue(evidence, EvidenceIssueKind.Missing, "main was not found", BuildKey);

        var result = GameIdentityResolver.Resolve(EmbeddedCatalog(), evidence);
        Assert.Equal(IdentificationStatus.Unknown, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Contains("identity matches 'zelda-ocarina-of-time', but build fact(s) are missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Program_id_with_malformed_build_resolves_to_unknown()
    {
        var padding = new byte[12];
        padding[0] = 1;
        using var game = new TempGame(Npdm(), TempGame.WithBody(NsoHeader(padding12: padding)));
        var evidence = await Collect(game);

        Assert.True(evidence.Facts.ContainsKey(ProgramKey));
        Assert.False(evidence.Facts.ContainsKey(BuildKey));
        AssertIssue(evidence, EvidenceIssueKind.Malformed, "non-zero bytes after the first 20", BuildKey);
        Assert.Equal(IdentificationStatus.Unknown, GameIdentityResolver.Resolve(EmbeddedCatalog(), evidence).Status);
    }

    [Fact]
    public async Task Build_without_program_id_resolves_to_unknown()
    {
        using var game = new TempGame(npdm: null, TempGame.WithBody(NsoHeader()));
        var evidence = await Collect(game);

        Assert.Equal(OotBuildIdHex, Assert.Single(evidence.Facts).Value);
        AssertIssue(evidence, EvidenceIssueKind.Missing, "main.npdm was not found", ProgramKey);
        Assert.Equal(IdentificationStatus.Unknown, GameIdentityResolver.Resolve(EmbeddedCatalog(), evidence).Status);
    }

    [Fact]
    public async Task Well_formed_unsupported_program_id_is_evidence_and_resolves_to_unsupported()
    {
        using var game = new TempGame(Npdm(programId: 0x0100000000010000), TempGame.WithBody(NsoHeader()));
        var evidence = await Collect(game);

        Assert.Equal("0100000000010000", evidence.Facts[ProgramKey]);
        Assert.Empty(evidence.Issues); // the provider does not judge support
        Assert.Equal(IdentificationStatus.Unsupported, GameIdentityResolver.Resolve(EmbeddedCatalog(), evidence).Status);
    }

    [Fact]
    public async Task Well_formed_unsupported_build_is_evidence_and_resolves_to_unsupported()
    {
        var other = Convert.FromHexString("1111111111111111111111111111111111111111");
        using var game = new TempGame(Npdm(), TempGame.WithBody(NsoHeader(buildId20: other)));
        var evidence = await Collect(game);

        Assert.Equal("1111111111111111111111111111111111111111", evidence.Facts[BuildKey]);
        Assert.Empty(evidence.Issues);
        Assert.Equal(IdentificationStatus.Unsupported, GameIdentityResolver.Resolve(EmbeddedCatalog(), evidence).Status);
    }

    [Fact]
    public async Task Malformed_files_never_produce_facts()
    {
        using var game = new TempGame(Npdm(metaMagic: "XXXX"), TempGame.WithBody(NsoHeader(magic: "XXXX")));
        var evidence = await Collect(game);

        Assert.Empty(evidence.Facts);
        AssertIssue(evidence, EvidenceIssueKind.Malformed, "does not start with the META magic", ProgramKey);
        AssertIssue(evidence, EvidenceIssueKind.Malformed, "does not start with the NSO0 magic", BuildKey);
        Assert.DoesNotContain(evidence.Issues, i => i.Kind == EvidenceIssueKind.Conflict);
        Assert.Equal(IdentificationStatus.Unknown, GameIdentityResolver.Resolve(EmbeddedCatalog(), evidence).Status);
    }

    [Fact]
    public async Task Invalid_process_fingerprint_is_reported_and_resolves_to_unknown()
    {
        using var game = TempGame.Valid();
        var evidence = await RealProvider().CollectAsync(Selected(game.ExecutablePath, pid: 0, start: default(DateTime)), CancellationToken.None);

        Assert.Equal(new ProcessFingerprint(0, default), evidence.Process);
        AssertIssue(evidence, EvidenceIssueKind.Other, "no valid process fingerprint");
        Assert.Equal(IdentificationStatus.Unknown, GameIdentityResolver.Resolve(EmbeddedCatalog(), evidence).Status);
    }

    // ---------------- file sizes and bounded reads ----------------

    [Fact]
    public async Task Oversized_npdm_is_rejected_without_reading_it()
    {
        using var game = new TempGame(Npdm(length: 64 * 1024 + 1), TempGame.WithBody(NsoHeader()));
        var evidence = await Collect(game);
        AssertIssue(evidence, EvidenceIssueKind.Malformed, "main.npdm exceeds the 65536-byte size limit", ProgramKey);
        Assert.False(evidence.Facts.ContainsKey(ProgramKey));
        Assert.True(evidence.Facts.ContainsKey(BuildKey));
    }

    [Fact]
    public async Task Truncated_npdm_is_malformed()
    {
        using var game = new TempGame(Npdm(length: 0x40), TempGame.WithBody(NsoHeader()));
        AssertIssue(await Collect(game), EvidenceIssueKind.Malformed, "main.npdm is shorter than the expected 0x80 bytes", ProgramKey);
    }

    [Theory]
    [InlineData(0x5F)]
    [InlineData(0x60)]
    [InlineData(0xFF)]
    public async Task Truncated_nso_files_are_malformed(int length)
    {
        using var game = new TempGame(Npdm(), NsoHeader(length: length));
        var evidence = await Collect(game);
        AssertIssue(evidence, EvidenceIssueKind.Malformed, "main is shorter than the expected 0x100 bytes", BuildKey);
        Assert.False(evidence.Facts.ContainsKey(BuildKey));
    }

    [Fact]
    public async Task Large_nso_reads_only_the_fixed_header()
    {
        var huge = new HugeFileStream(NsoHeader(), reportedLength: 50L * 1024 * 1024 * 1024); // "50 GiB"
        var files = new ScriptedFileAccess()
            .Npdm(() => new MemoryStream(Npdm()))
            .Nso(() => huge);

        var evidence = await ScriptedProvider(files).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), CancellationToken.None);

        Assert.Equal(OotBuildIdHex, evidence.Facts[BuildKey]);
        Assert.Equal(0x100, huge.TotalBytesRead);
    }

    // ---------------- file-system failures ----------------

    [Fact]
    public async Task Missing_exefs_folder_reports_both_facts_missing()
    {
        using var game = new TempGame(Npdm(), NsoHeader(), createExefs: false);
        var evidence = await Collect(game);

        Assert.Empty(evidence.Facts);
        AssertIssue(evidence, EvidenceIssueKind.Missing, "the .longshot_exefs folder containing main.npdm was not found", ProgramKey);
        AssertIssue(evidence, EvidenceIssueKind.Missing, "the .longshot_exefs folder containing main was not found", BuildKey);
    }

    [Fact]
    public async Task Missing_npdm_and_missing_nso_are_reported_independently()
    {
        using var noNpdm = new TempGame(npdm: null, TempGame.WithBody(NsoHeader()));
        AssertIssue(await Collect(noNpdm), EvidenceIssueKind.Missing, "main.npdm was not found", ProgramKey);

        using var noNso = new TempGame(Npdm(), nso: null);
        AssertIssue(await Collect(noNso), EvidenceIssueKind.Missing, "main was not found", BuildKey);
    }

    [Fact]
    public async Task Access_denied_is_unreadable()
    {
        var files = new ScriptedFileAccess()
            .Npdm(() => throw new UnauthorizedAccessException("C:\\Users\\secret\\main.npdm"))
            .Nso(() => new MemoryStream(NsoHeader()));

        var evidence = await ScriptedProvider(files).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), CancellationToken.None);

        AssertIssue(evidence, EvidenceIssueKind.Unreadable, "access to main.npdm was denied", ProgramKey);
        Assert.True(evidence.Facts.ContainsKey(BuildKey));
        Assert.DoesNotContain(evidence.Issues, i => i.Message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sharing_violation_is_unreadable()
    {
        using var game = TempGame.Valid();
        await using (new FileStream(game.NpdmPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var evidence = await Collect(game);
            AssertIssue(evidence, EvidenceIssueKind.Unreadable, "main.npdm could not be read (I/O error)", ProgramKey);
            Assert.Equal(OotBuildIdHex, evidence.Facts[BuildKey]);
        }
    }

    [Fact]
    public async Task File_disappearing_on_open_is_missing()
    {
        var files = new ScriptedFileAccess()
            .Npdm(() => new MemoryStream(Npdm()))
            .Nso(() => throw new FileNotFoundException("gone"));

        var evidence = await ScriptedProvider(files).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), CancellationToken.None);

        AssertIssue(evidence, EvidenceIssueKind.Missing, "main was not found", BuildKey);
        Assert.Equal(OotProgramIdHex, evidence.Facts[ProgramKey]);
    }

    [Fact]
    public async Task File_removed_from_disk_before_collection_is_missing()
    {
        using var game = TempGame.Valid();
        File.Delete(game.NsoPath);
        AssertIssue(await Collect(game), EvidenceIssueKind.Missing, "main was not found", BuildKey);
    }

    [Fact]
    public async Task Stream_ending_during_the_read_is_malformed()
    {
        var files = new ScriptedFileAccess()
            .Npdm(() => new TruncatedStream(Npdm().Take(0x100).ToArray(), reportedLength: 0x724))
            .Nso(() => new TruncatedStream(NsoHeader().Take(0x80).ToArray(), reportedLength: 0x1000));

        var evidence = await ScriptedProvider(files).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), CancellationToken.None);

        Assert.Empty(evidence.Facts);
        AssertIssue(evidence, EvidenceIssueKind.Malformed, "main.npdm ended early", ProgramKey);
        AssertIssue(evidence, EvidenceIssueKind.Malformed, "main ended early", BuildKey);
    }

    [Fact]
    public async Task Generic_io_error_is_unreadable()
    {
        var files = new ScriptedFileAccess()
            .Npdm(() => throw new IOException("device not ready: C:\\Users\\secret"))
            .Nso(() => new MemoryStream(NsoHeader()));

        var evidence = await ScriptedProvider(files).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), CancellationToken.None);
        AssertIssue(evidence, EvidenceIssueKind.Unreadable, "main.npdm could not be read (I/O error)", ProgramKey);
        Assert.DoesNotContain(evidence.Issues, i => i.Message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unexpected_exceptions_are_not_swallowed()
    {
        var files = new ScriptedFileAccess().Npdm(() => throw new InvalidOperationException("bug"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScriptedProvider(files).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), CancellationToken.None));
    }

    // ---------------- deadline and cancellation ----------------

    [Fact]
    public async Task Provider_deadline_produces_unreadable_issues_not_cancellation()
    {
        var blocking = new BlockingStream(0x724);
        var files = new ScriptedFileAccess().Npdm(() => blocking).Nso(() => new MemoryStream(NsoHeader()));
        var time = new ManualTimeProvider();

        var collecting = ScriptedProvider(files, time).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), CancellationToken.None);
        await blocking.ReadStarted.Task.WaitAsync(WaitLimit);
        Assert.Contains(LongshotGameIdentityProvider.DefaultTimeout, time.ActiveDueTimes); // one total 5 s deadline
        time.FireAll();

        var evidence = await collecting.WaitAsync(WaitLimit);
        await blocking.ReadEnded.Task.WaitAsync(WaitLimit); // the blocked read finished; nothing left running

        Assert.Empty(evidence.Facts);
        AssertIssue(evidence, EvidenceIssueKind.Unreadable, "identity collection timed out after 5 s while reading main.npdm", ProgramKey);
        AssertIssue(evidence, EvidenceIssueKind.Unreadable, "main was not read because identity collection timed out", BuildKey);
        Assert.Equal(new[] { "main.npdm" }, files.Opened); // main was never opened after the deadline
    }

    [Fact]
    public async Task Deadline_during_the_second_file_keeps_the_first_fact()
    {
        var blocking = new BlockingStream(0x1000);
        var files = new ScriptedFileAccess().Npdm(() => new MemoryStream(Npdm())).Nso(() => blocking);
        var time = new ManualTimeProvider();

        var collecting = ScriptedProvider(files, time).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), CancellationToken.None);
        await blocking.ReadStarted.Task.WaitAsync(WaitLimit);
        time.FireAll();
        var evidence = await collecting.WaitAsync(WaitLimit);

        Assert.Equal(OotProgramIdHex, Assert.Single(evidence.Facts).Value);
        AssertIssue(evidence, EvidenceIssueKind.Unreadable, "identity collection timed out after 5 s while reading main", BuildKey);
        Assert.Equal(IdentificationStatus.Unknown, GameIdentityResolver.Resolve(EmbeddedCatalog(), evidence).Status);
    }

    [Fact]
    public async Task Caller_cancellation_during_a_read_propagates()
    {
        var blocking = new BlockingStream(0x724);
        var files = new ScriptedFileAccess().Npdm(() => blocking).Nso(() => new MemoryStream(NsoHeader()));
        using var cts = new CancellationTokenSource();

        var collecting = ScriptedProvider(files).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), cts.Token);
        await blocking.ReadStarted.Task.WaitAsync(WaitLimit);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collecting.WaitAsync(WaitLimit));
        await blocking.ReadEnded.Task.WaitAsync(WaitLimit);
    }

    [Fact]
    public async Task Already_cancelled_caller_token_propagates_without_any_file_access()
    {
        var files = new ScriptedFileAccess().Npdm(() => new MemoryStream(Npdm())).Nso(() => new MemoryStream(NsoHeader()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScriptedProvider(files).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), cts.Token));
        Assert.Empty(files.Opened);
    }

    [Fact]
    public async Task Caller_cancellation_wins_over_a_simultaneous_deadline()
    {
        var blocking = new BlockingStream(0x724);
        var files = new ScriptedFileAccess().Npdm(() => blocking);
        var time = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();

        var collecting = ScriptedProvider(files, time).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), cts.Token);
        await blocking.ReadStarted.Task.WaitAsync(WaitLimit);
        cts.Cancel();
        time.FireAll();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collecting.WaitAsync(WaitLimit));
    }

    [Fact]
    public void Non_positive_timeouts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LongshotGameIdentityProvider(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LongshotGameIdentityProvider(TimeSpan.FromSeconds(-1)));
        Assert.Equal(TimeSpan.FromSeconds(5), LongshotGameIdentityProvider.DefaultTimeout);
    }

    [Fact]
    public async Task Null_selected_process_throws()
        => await Assert.ThrowsAsync<ArgumentNullException>(() => new LongshotGameIdentityProvider().CollectAsync(null!, CancellationToken.None));

    // ---------------- executable path validation ----------------

    [Theory]
    [InlineData("", "the selected executable path is empty")]
    [InlineData("   ", "the selected executable path is empty")]
    [InlineData(@"bin\longshot.exe", "is not fully qualified")]
    [InlineData(@"C:longshot\bin\longshot.exe", "is not fully qualified")]
    [InlineData(@"C:\Games\Longshot\bin\..\bin\longshot.exe", "is not normalized")]
    [InlineData(@"C:\Games\Longshot\.\bin\longshot.exe", "is not normalized")]
    [InlineData(@"C:/Games/Longshot/bin/longshot.exe", "is not normalized")]
    [InlineData(@"\\?\C:\Games\Longshot\bin\longshot.exe", "device-namespace executable paths are not supported")]
    [InlineData(@"\\.\C:\Games\Longshot\bin\longshot.exe", "device-namespace executable paths are not supported")]
    [InlineData(@"\\server\share\Longshot\bin\longshot.exe", "network (UNC) executable paths are not supported")]
    [InlineData(@"C:\Games\Longshot\bin\game.exe", "is not a Longshot game binary")]
    [InlineData(@"C:\Games\Longshot\longshot.exe", "is not a Longshot game binary")]          // the launcher stub
    [InlineData(@"C:\Games\Longshot\other\longshot.exe", "is not a Longshot game binary")]    // no bin folder
    public async Task Invalid_executable_paths_are_malformed_and_never_touch_the_file_system(string path, string fragment)
    {
        var files = new ScriptedFileAccess().Npdm(() => new MemoryStream(Npdm())).Nso(() => new MemoryStream(NsoHeader()));
        var evidence = await ScriptedProvider(files).CollectAsync(Selected(path), CancellationToken.None);

        Assert.Empty(evidence.Facts);
        var issue = AssertIssue(evidence, EvidenceIssueKind.Malformed, fragment);
        Assert.Null(issue.FactKey);
        Assert.Empty(files.Opened);
        if (path.Trim().Length > 0)
            Assert.DoesNotContain(evidence.Issues, i => i.Message.Contains(path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Network_drive_installations_are_rejected()
    {
        var files = new ScriptedFileAccess { NetworkDrive = true }
            .Npdm(() => new MemoryStream(Npdm())).Nso(() => new MemoryStream(NsoHeader()));
        var evidence = await ScriptedProvider(files).CollectAsync(Selected(@"Z:\Games\Longshot\bin\longshot.exe"), CancellationToken.None);

        AssertIssue(evidence, EvidenceIssueKind.Malformed, "network-drive installations are not supported");
        Assert.Empty(files.Opened);
    }

    [Fact]
    public async Task Files_are_read_only_from_the_selected_installation()
    {
        var files = new ScriptedFileAccess().Npdm(() => new MemoryStream(Npdm())).Nso(() => new MemoryStream(NsoHeader()));
        await ScriptedProvider(files).CollectAsync(Selected(@"C:\Games\Longshot\bin\longshot.exe"), CancellationToken.None);
        Assert.Equal(new[] { "main.npdm", "main" }, files.Opened);

        var provider = ScriptedProvider(files);
        Assert.True(provider.TryGetExefsDirectory(@"C:\Games\Longshot\bin\longshot.exe", out var exefs, out _));
        Assert.Equal(@"C:\Games\Longshot\.longshot_exefs", exefs);
    }

    // ---------------- no modification, no path leakage ----------------

    [Fact]
    public async Task Read_only_files_work_and_nothing_is_modified()
    {
        using var game = TempGame.Valid();
        File.SetAttributes(game.NpdmPath, FileAttributes.ReadOnly);
        File.SetAttributes(game.NsoPath, FileAttributes.ReadOnly);
        var before = Snapshot(game);

        var evidence = await Collect(game);

        Assert.Equal(2, evidence.Facts.Count);
        Assert.Equal(before, Snapshot(game));
        Assert.True(File.GetAttributes(game.NpdmPath).HasFlag(FileAttributes.ReadOnly));
    }

    private static string Snapshot(TempGame game)
        => string.Join("|", new[] { game.NpdmPath, game.NsoPath, game.ExecutablePath }.Select(p =>
        {
            var info = new FileInfo(p);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))}";
        }))
           + "|" + string.Join(",", Directory.EnumerateFileSystemEntries(game.Root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal));

    [Fact]
    public async Task Issue_messages_never_contain_the_installation_path()
    {
        using var missingAll = new TempGame(Npdm(), NsoHeader(), createExefs: false);
        using var malformed = new TempGame(Npdm(metaMagic: "XXXX"), NsoHeader(length: 0x10));
        using var oversized = new TempGame(Npdm(length: 70_000), nso: null);

        foreach (var game in new[] { missingAll, malformed, oversized })
        {
            var evidence = await Collect(game);
            Assert.NotEmpty(evidence.Issues);
            Assert.All(evidence.Issues, i =>
            {
                Assert.DoesNotContain(game.Root, i.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\'), i.Message, StringComparison.OrdinalIgnoreCase);
            });
        }
    }
}
