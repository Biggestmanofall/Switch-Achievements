using System.Text;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;
using SwitchAchievements.Tests.Fakes;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.LiveTrackingSetup;

/// <summary>Persistent-environment reader for tests (never touches the real environment).</summary>
internal sealed class FakePersistentEnvironment : IPersistentEnvironment
{
    public string? User { get; set; }
    public string? Machine { get; set; }
    public Exception? Throw { get; set; }
    public List<(string Name, EnvironmentVariableTarget Target)> Reads { get; } = new();

    public string? Read(string name, EnvironmentVariableTarget target)
    {
        lock (Reads) Reads.Add((name, target));
        if (Throw is not null)
            throw Throw;
        return target switch
        {
            EnvironmentVariableTarget.User => User,
            EnvironmentVariableTarget.Machine => Machine,
            _ => throw new InvalidOperationException("only persistent targets are read"),
        };
    }
}

/// <summary>A throwaway Longshot-shaped game folder with a game process pointing into it.</summary>
internal sealed class SetupRig : IDisposable
{
    public SetupRig()
    {
        Root = Path.Combine(Path.GetTempPath(), "sa-setup-tests", Guid.NewGuid().ToString("N"));
        GameDirectory = Path.Join(Root, "Longshot");
        Directory.CreateDirectory(Path.Join(GameDirectory, "bin"));
        File.WriteAllBytes(Path.Join(GameDirectory, "image.bin"), new byte[] { 1, 2, 3 });
        Process = Game(100);
        Provider.Processes = new[] { Process };
    }

    public string Root { get; }
    public string GameDirectory { get; }
    public string EnvPath => Path.Join(GameDirectory, LongshotEnvFile.FileName);
    public GameProcessInfo Process { get; }
    public FakeGameIntegration Integration { get; } = new();
    public FakeProcessProvider Provider { get; } = new();
    public FakePersistentEnvironment Environment { get; } = new();
    public List<string> Log { get; } = new();

    public GameProcessInfo Game(int pid, DateTime? start = null)
        => new(pid, Path.Join(GameDirectory, "bin", "longshot.exe"), start ?? new DateTime(2026, 11, 24, 5, 0, 0, DateTimeKind.Utc));

    public static IdentifiedGame Identified(GameProcessInfo process)
        => GameIdentityResolver.Resolve(IdentityTestData.OotCatalog(), IdentityEvidence.Oot(process)).Game!;

    public IdentifiedGame Oot => Identified(Process);

    public LongshotLiveTrackingSetup Create(IEnvFileSystem? fs = null)
        => new(Integration, Provider, line => { lock (Log) Log.Add(line); }, Environment, new LongshotEnvFile(fs), _ => false, File.Exists);

    public bool EnvFileExists => File.Exists(EnvPath);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}

/// <summary>M5.8.2: the Longshot live-tracking setup (eligibility, process, folder, environment, file).</summary>
public sealed class LongshotLiveTrackingSetupTests : IDisposable
{
    private readonly SetupRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private LiveTrackingSetupResult Enable(IdentifiedGame? game = null, IEnvFileSystem? fs = null, CancellationToken ct = default)
        => _rig.Create(fs).Enable(game ?? _rig.Oot, ct);

    // ---------------- success ----------------

    [Fact]
    public void The_identified_process_enables_live_tracking_in_its_game_folder()
    {
        Assert.Equal(LiveTrackingSetupResult.Enabled, Enable());

        Assert.True(_rig.EnvFileExists);
        Assert.Equal(LongshotEnvFile.SettingValue, LongshotEnvFile.EffectiveControlValue(File.ReadAllBytes(_rig.EnvPath)));
        Assert.Single(Directory.GetFiles(_rig.GameDirectory, "*.txt")); // the only file added
    }

    [Fact]
    public void Enabling_again_is_already_enabled_and_writes_nothing()
    {
        Assert.Equal(LiveTrackingSetupResult.Enabled, Enable());
        var bytes = File.ReadAllBytes(_rig.EnvPath);

        Assert.Equal(LiveTrackingSetupResult.AlreadyEnabled, Enable());
        Assert.Equal(bytes, File.ReadAllBytes(_rig.EnvPath));
    }

    // ---------------- eligibility and process ----------------

    [Fact]
    public void A_game_of_another_integration_is_not_eligible()
    {
        var other = GameIdentityResolver.Resolve(
            new GameCatalog(new[] { IdentityTestData.OtherRecompGame() }),
            IdentityTestData.Evidence(
                IdentityTestData.Facts((IdentityTestData.OtherIdentityKey, "sha256:abc123"), (IdentityTestData.OtherBuildKey, "r42")),
                IdentityTestData.OtherIntegration,
                new ProcessFingerprint(_rig.Process.ProcessId, _rig.Process.ProcessStartUtc))).Game;
        Assert.NotNull(other);

        Assert.Equal(LiveTrackingSetupResult.NotEligible, Enable(other));
        Assert.False(_rig.EnvFileExists);
    }

    [Fact]
    public void A_stale_fingerprint_is_rejected()
    {
        // The game was identified for process 100, which has since been replaced by 200.
        _rig.Provider.Processes = new[] { _rig.Game(200) };
        Assert.Equal(LiveTrackingSetupResult.ProcessChanged, Enable());
        Assert.False(_rig.EnvFileExists);
    }

    [Fact]
    public void The_same_pid_with_another_start_time_is_another_process()
    {
        _rig.Provider.Processes = new[] { _rig.Game(100, new DateTime(2026, 11, 24, 6, 0, 0, DateTimeKind.Utc)) };
        Assert.Equal(LiveTrackingSetupResult.ProcessChanged, Enable());
        Assert.False(_rig.EnvFileExists);
    }

    [Fact]
    public void An_ambiguous_selection_is_rejected()
    {
        _rig.Provider.Processes = new[] { _rig.Process, _rig.Game(200) };
        Assert.Equal(LiveTrackingSetupResult.ProcessChanged, Enable());
        Assert.False(_rig.EnvFileExists);
    }

    [Fact]
    public void A_missing_process_is_rejected()
    {
        _rig.Provider.Processes = Array.Empty<GameProcessInfo>();
        Assert.Equal(LiveTrackingSetupResult.ProcessChanged, Enable());
        Assert.False(_rig.EnvFileExists);
    }

    [Fact]
    public void A_process_change_immediately_before_writing_is_rejected()
    {
        var calls = 0;
        _rig.Provider.OnGetProcesses = _ => Interlocked.Increment(ref calls) == 1
            ? new[] { _rig.Process }   // first check: still the identified process
            : new[] { _rig.Game(200) }; // the check just before writing: replaced

        Assert.Equal(LiveTrackingSetupResult.ProcessChanged, Enable());
        Assert.Equal(2, calls);
        Assert.False(_rig.EnvFileExists);
    }

    [Fact]
    public void Excluded_helpers_do_not_count_as_the_game()
    {
        // The integration's own candidate filter is used, exactly as monitoring does.
        _rig.Integration.SelectCandidatesOverride = _ => Array.Empty<GameProcessInfo>();
        Assert.Equal(LiveTrackingSetupResult.ProcessChanged, Enable());
    }

    [Fact]
    public void A_folder_without_image_bin_is_unavailable_and_the_reason_has_no_path()
    {
        File.Delete(Path.Join(_rig.GameDirectory, "image.bin"));

        Assert.Equal(LiveTrackingSetupResult.GameFolderUnavailable, Enable());
        Assert.False(_rig.EnvFileExists);
        var line = Assert.Single(_rig.Log);
        Assert.Contains("no image.bin", line, StringComparison.Ordinal);
        Assert.DoesNotContain(_rig.Root, line, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- persistent environment ----------------

    [Fact]
    public void No_persistent_value_lets_the_file_be_written_and_both_targets_are_read()
    {
        Assert.Equal(LiveTrackingSetupResult.Enabled, Enable());
        Assert.Contains(("HOS_CONTROL", EnvironmentVariableTarget.User), _rig.Environment.Reads);
        Assert.Contains(("HOS_CONTROL", EnvironmentVariableTarget.Machine), _rig.Environment.Reads);
    }

    [Theory]
    [InlineData("longshot_control", null)]
    [InlineData(null, "longshot_control")]
    [InlineData("longshot_control", "longshot_control")]
    public void A_matching_persistent_value_is_already_enabled_without_a_file_entry(string? user, string? machine)
    {
        _rig.Environment.User = user;
        _rig.Environment.Machine = machine;

        Assert.Equal(LiveTrackingSetupResult.AlreadyEnabledByEnvironment, Enable());
        Assert.False(_rig.EnvFileExists);
    }

    [Theory]
    [InlineData("other_pipe", null)]
    [InlineData(null, "other_pipe")]
    [InlineData("", null)]                          // empty: Longshot would turn the bridge off, and it takes priority
    [InlineData("longshot_control", "other_pipe")]  // User and Machine disagree: refused conservatively
    [InlineData("other_pipe", "longshot_control")]
    public void A_conflicting_persistent_value_refuses_to_write(string? user, string? machine)
    {
        _rig.Environment.User = user;
        _rig.Environment.Machine = machine;

        Assert.Equal(LiveTrackingSetupResult.EnvironmentConflict, Enable());
        Assert.False(_rig.EnvFileExists);
    }

    [Fact]
    public void An_unreadable_environment_refuses_to_write()
    {
        _rig.Environment.Throw = new System.Security.SecurityException("denied");

        Assert.Equal(LiveTrackingSetupResult.EnvironmentUnreadable, Enable());
        Assert.False(_rig.EnvFileExists);
        Assert.Contains(_rig.Log, l => l.Contains("SecurityException", StringComparison.Ordinal));
    }

    // ---------------- file results ----------------

    [Fact]
    public void A_conflicting_file_value_is_a_file_conflict()
    {
        File.WriteAllText(_rig.EnvPath, "HOS_CONTROL=other\n");
        Assert.Equal(LiveTrackingSetupResult.FileConflict, Enable());
        Assert.Equal("HOS_CONTROL=other\n", File.ReadAllText(_rig.EnvPath));
    }

    [Fact]
    public void An_unsupported_file_is_reported()
    {
        File.WriteAllBytes(_rig.EnvPath, new byte[] { 0xFF, 0xFE, (byte)'#', 0 });
        Assert.Equal(LiveTrackingSetupResult.FileUnsupported, Enable());
    }

    [Fact]
    public void A_read_only_file_is_access_denied()
    {
        File.WriteAllText(_rig.EnvPath, "RX_VK_PACE=1\n");
        File.SetAttributes(_rig.EnvPath, FileAttributes.ReadOnly);
        Assert.Equal(LiveTrackingSetupResult.AccessDenied, Enable());
    }

    [Fact]
    public void A_file_open_elsewhere_is_in_use()
    {
        File.WriteAllText(_rig.EnvPath, "RX_VK_PACE=1\n");
        using var _ = new FileStream(_rig.EnvPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(LiveTrackingSetupResult.FileInUse, Enable());
    }

    [Fact]
    public void An_unreadable_file_is_a_read_failure()
    {
        File.WriteAllText(_rig.EnvPath, "RX_VK_PACE=1\n");
        var fs = new ScriptedFileSystem(WindowsEnvFileSystem.Instance)
        {
            OpenException = new IOException("Data error (cyclic redundancy check).", unchecked((int)0x80070017)),
        };
        Assert.Equal(LiveTrackingSetupResult.ReadFailed, Enable(fs: fs));
    }

    [Fact]
    public void A_failed_write_that_was_restored_is_a_write_failure()
    {
        File.WriteAllText(_rig.EnvPath, "RX_VK_PACE=1\n");
        var fs = new ScriptedFileSystem(WindowsEnvFileSystem.Instance) { PartialAppendThenFail = true };
        Assert.Equal(LiveTrackingSetupResult.WriteFailed, Enable(fs: fs));
        Assert.Equal("RX_VK_PACE=1\n", File.ReadAllText(_rig.EnvPath));
    }

    [Fact]
    public void A_failed_write_that_could_not_be_restored_is_a_recovery_failure()
    {
        File.WriteAllText(_rig.EnvPath, "RX_VK_PACE=1\n");
        var fs = new ScriptedFileSystem(WindowsEnvFileSystem.Instance) { PartialAppendThenFail = true, FailTruncate = true };
        Assert.Equal(LiveTrackingSetupResult.RecoveryFailed, Enable(fs: fs));
    }

    // ---------------- concurrency and cancellation ----------------

    [Fact]
    public void Simultaneous_requests_write_one_setting()
    {
        var setup = _rig.Create();
        var results = new LiveTrackingSetupResult[6];
        Parallel.For(0, results.Length, i => results[i] = setup.Enable(_rig.Oot, CancellationToken.None));

        Assert.Single(results, r => r == LiveTrackingSetupResult.Enabled);
        Assert.All(results, r => Assert.Contains(r, new[] { LiveTrackingSetupResult.Enabled, LiveTrackingSetupResult.AlreadyEnabled }));
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(_rig.EnvPath));
        Assert.Equal(1, text.Split("HOS_CONTROL=longshot_control").Length - 1);
    }

    [Fact]
    public void Cancellation_before_writing_leaves_the_folder_untouched()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(LiveTrackingSetupResult.Cancelled, Enable(ct: cts.Token));
        Assert.False(_rig.EnvFileExists);
    }

    [Fact]
    public void Cancellation_during_process_revalidation_leaves_the_folder_untouched()
    {
        using var cts = new CancellationTokenSource();
        _rig.Provider.OnGetProcesses = _ => { cts.Cancel(); return new[] { _rig.Process }; };

        Assert.Equal(LiveTrackingSetupResult.Cancelled, Enable(ct: cts.Token));
        Assert.False(_rig.EnvFileExists);
    }
}
