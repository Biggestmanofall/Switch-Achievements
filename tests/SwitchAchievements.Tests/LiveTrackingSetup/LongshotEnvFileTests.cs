using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.Tests.LiveTrackingSetup;

/// <summary>
/// M5.8.2: safe, append-only editing of a game's longshot_env.txt. Every test works in its
/// own temporary folder (never a real Longshot installation).
/// </summary>
public sealed class LongshotEnvFileTests : IDisposable
{
    private const string Line = "HOS_CONTROL=longshot_control";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-envfile-tests", Guid.NewGuid().ToString("N"));

    public LongshotEnvFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string EnvPath => Path.Join(_dir, LongshotEnvFile.FileName);

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    private static string Added(string eol) => LongshotEnvFile.Comment + eol + Line + eol;

    private EnvFileOutcome Ensure(IEnvFileSystem? fs = null, CancellationToken ct = default)
        => new LongshotEnvFile(fs).EnsureControlSetting(_dir, ct);

    private void Write(byte[] content) => File.WriteAllBytes(EnvPath, content);

    private byte[] Read() => File.ReadAllBytes(EnvPath);

    // ---------------- creation ----------------

    [Fact]
    public void A_missing_file_is_created_with_the_comment_and_setting()
    {
        Assert.Equal(EnvFileOutcome.Created, Ensure());

        var content = Read();
        Assert.Equal(Bytes(Added("\r\n")), content);
        Assert.False(content.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })); // UTF-8 without BOM
        Assert.Equal(LongshotEnvFile.SettingValue, LongshotEnvFile.EffectiveControlValue(content));
    }

    [Fact]
    public void An_empty_file_receives_the_setting()
    {
        Write(Array.Empty<byte>());
        Assert.Equal(EnvFileOutcome.Appended, Ensure());
        Assert.Equal(Bytes(Added("\r\n")), Read());
    }

    [Fact]
    public void A_file_created_concurrently_after_the_existence_check_is_never_overwritten()
    {
        var original = Bytes("# someone else's file\nRX_VK_PACE=1\n");
        var fs = new ScriptedFileSystem(WindowsEnvFileSystemForTests.Instance)
        {
            // The probe says "missing"; by the time we create, another program has created it.
            BeforeCreate = path => File.WriteAllBytes(path, original),
            ProbeOverride = (_, call) => call == 1 ? EnvPathKind.Missing : null,
        };

        Assert.Equal(EnvFileOutcome.Appended, Ensure(fs));
        Assert.Equal(original.Concat(Bytes(Added("\n"))).ToArray(), Read()); // appended to theirs, not replaced
    }

    // ---------------- idempotence and conflicts ----------------

    [Fact]
    public void An_existing_correct_setting_writes_nothing()
    {
        Write(Bytes("# mine\r\n" + Line + "\r\n"));
        var before = File.GetLastWriteTimeUtc(EnvPath);
        var bytes = Read();

        Assert.Equal(EnvFileOutcome.AlreadyPresent, Ensure());

        Assert.Equal(bytes, Read());
        Assert.Equal(before, File.GetLastWriteTimeUtc(EnvPath));
    }

    [Fact]
    public void Duplicate_correct_settings_are_accepted_without_another_line()
    {
        var content = Bytes(Line + "\n" + Line + "\n");
        Write(content);
        Assert.Equal(EnvFileOutcome.AlreadyPresent, Ensure());
        Assert.Equal(content, Read());
    }

    [Theory]
    [InlineData("HOS_CONTROL=other_pipe\n")]
    [InlineData("HOS_CONTROL=\n")]                                       // empty: Longshot turns the bridge off
    [InlineData("HOS_CONTROL=longshot_control\nHOS_CONTROL=other\n")]   // the last line wins
    [InlineData("  HOS_CONTROL=other  \r\n")]                            // leading/trailing blanks trimmed as Longshot does
    [InlineData("\uFEFFHOS_CONTROL=other\n")]                            // after a UTF-8 BOM (written as UTF-8 below)
    public void A_conflicting_effective_value_is_never_overwritten(string text)
    {
        var content = text.StartsWith('\uFEFF') ? Encoding.UTF8.GetBytes(text) : Bytes(text);
        Write(content);

        Assert.Equal(EnvFileOutcome.Conflict, Ensure());
        Assert.Equal(content, Read());
    }

    [Fact]
    public void A_conflicting_earlier_value_followed_by_the_correct_one_is_already_present()
    {
        var content = Bytes("HOS_CONTROL=other\nHOS_CONTROL=longshot_control\n");
        Write(content);
        Assert.Equal(EnvFileOutcome.AlreadyPresent, Ensure());
        Assert.Equal(content, Read());
    }

    [Theory]
    [InlineData("# HOS_CONTROL=other\n")]                 // a comment
    [InlineData("   # HOS_CONTROL=other\n")]              // an indented comment
    [InlineData("HOS_CONTROL =other\n")]                  // the name is exactly the text before '=' (Longshot leaves this out)
    [InlineData("hos_control=other\n")]                   // names are case-sensitive in the file
    [InlineData("HOS_CONTROL\n")]                         // no '='
    [InlineData("HOS_CONTROL=C:\\pipes\\x\n")]            // a path: Longshot leaves the line out
    [InlineData("HOS_CONTROL=..\n")]
    [InlineData("HOS_CONTROL_MEM=ro\n")]                  // another switch
    public void Lines_Longshot_ignores_do_not_count(string text)
    {
        var content = Bytes(text);
        Write(content);

        Assert.Equal(EnvFileOutcome.Appended, Ensure());
        Assert.Equal(content.Concat(Bytes(Added("\n"))).ToArray(), Read());
    }

    // ---------------- preservation ----------------

    [Theory]
    [InlineData("# notes\r\nRX_VK_PACE=1\r\n", "\r\n", "")]
    [InlineData("# notes\nRX_VK_PACE=1\n", "\n", "")]
    [InlineData("# notes\r\nRX_VK_PACE=1\nHOS_WIDE_UI=1\n", "\r\n", "")]   // mixed: the first line break decides
    [InlineData("# notes\nRX_VK_PACE=1", "\n", "\n")]                      // no final line break: one is added first
    [InlineData("RX_VK_PACE=1", "\r\n", "\r\n")]                            // a single line without a break: Windows style
    public void Existing_bytes_and_line_endings_are_preserved(string text, string eol, string separator)
    {
        var original = Bytes(text);
        Write(original);

        Assert.Equal(EnvFileOutcome.Appended, Ensure());

        var expected = original.Concat(Bytes(separator + Added(eol))).ToArray();
        Assert.Equal(expected, Read());
    }

    [Fact]
    public void A_utf8_bom_and_non_ascii_comments_are_untouched()
    {
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("# café — notes\r\nRX_VK_PACE=1\r\n")).ToArray();
        Write(original);

        Assert.Equal(EnvFileOutcome.Appended, Ensure());
        Assert.Equal(original.Concat(Bytes(Added("\r\n"))).ToArray(), Read());
    }

    [Fact]
    public void Ansi_bytes_in_comments_are_untouched()
    {
        var original = new byte[] { (byte)'#', (byte)' ', 0xE9, 0xFC, 0x80, 0xFF, (byte)'\n' };
        Write(original);

        Assert.Equal(EnvFileOutcome.Appended, Ensure());
        Assert.Equal(original.Concat(Bytes(Added("\n"))).ToArray(), Read());
    }

    // ---------------- unsupported files ----------------

    public static TheoryData<byte[]> UnsupportedContents => new()
    {
        new byte[] { 0xFF, 0xFE, (byte)'#', 0 },                     // UTF-16 LE BOM
        new byte[] { 0xFE, 0xFF, 0, (byte)'#' },                     // UTF-16 BE BOM
        Encoding.ASCII.GetBytes("RX_VK_PACE=1\n\0\n"),               // NUL byte
        Encoding.ASCII.GetBytes("# " + new string('x', 509) + "\n"), // 512 bytes with its line break: Longshot would split it
    };

    [Theory]
    [MemberData(nameof(UnsupportedContents))]
    public void Unsupported_contents_are_refused_and_untouched(byte[] content)
    {
        Write(content);
        Assert.Equal(EnvFileOutcome.Unsupported, Ensure());
        Assert.Equal(content, Read());
    }

    [Fact]
    public void The_longest_line_Longshot_reads_whole_is_accepted()
    {
        var content = Bytes("# " + new string('x', 508) + "\n"); // 511 bytes including the line break
        Write(content);
        Assert.Equal(EnvFileOutcome.Appended, Ensure());
    }

    [Fact]
    public void An_oversized_file_is_refused_and_untouched()
    {
        var content = Bytes(string.Concat(Enumerable.Repeat("# " + new string('x', 97) + "\n", 700))); // 70,000 bytes
        Write(content);
        Assert.Equal(EnvFileOutcome.Unsupported, Ensure());
        Assert.Equal(content, Read());
    }

    [Fact]
    public void A_directory_with_the_file_name_is_refused()
    {
        Directory.CreateDirectory(EnvPath);
        Assert.Equal(EnvFileOutcome.Unsupported, Ensure());
        Assert.True(Directory.Exists(EnvPath));
    }

    [Fact]
    public void A_junction_with_the_file_name_is_refused()
    {
        var target = Path.Join(_dir, "elsewhere");
        Directory.CreateDirectory(target);
        RunCmd($"mklink /J \"{EnvPath}\" \"{target}\"");
        try
        {
            Assert.True((File.GetAttributes(EnvPath) & FileAttributes.ReparsePoint) != 0);

            Assert.Equal(EnvFileOutcome.Unsupported, Ensure());
            Assert.Empty(Directory.EnumerateFileSystemEntries(target)); // nothing written through it
        }
        finally
        {
            RunCmd($"rmdir \"{EnvPath}\""); // removes the junction itself, never its target
        }
    }

    [Fact]
    public void A_hard_linked_file_is_refused_by_inspecting_the_opened_handle()
    {
        var other = Path.Join(_dir, "other-name.txt");
        var content = Bytes("RX_VK_PACE=1\n");
        File.WriteAllBytes(other, content);
        Assert.True(CreateHardLink(EnvPath, other, IntPtr.Zero));

        Assert.Equal(EnvFileOutcome.Unsupported, Ensure());
        Assert.Equal(content, File.ReadAllBytes(other));
    }

    [Theory]
    [InlineData((int)EnvHandleKind.ReparsePoint)]
    [InlineData((int)EnvHandleKind.Directory)]
    [InlineData((int)EnvHandleKind.MultipleLinks)]
    [InlineData((int)EnvHandleKind.Unknown)]
    public void A_file_replaced_between_the_path_check_and_the_open_is_refused(int openedAs)
    {
        var content = Bytes("RX_VK_PACE=1\n");
        Write(content);
        var fs = new ScriptedFileSystem(WindowsEnvFileSystemForTests.Instance) { OpenedKind = (EnvHandleKind)openedAs };

        Assert.Equal(EnvFileOutcome.Unsupported, Ensure(fs));
        Assert.Equal(content, Read());
        Assert.Equal(0, fs.Appends);
    }

    [Fact]
    public void A_real_regular_file_is_recognized_from_its_handle()
    {
        Write(Bytes("RX_VK_PACE=1\n"));
        using var handle = WindowsEnvFileSystemForTests.Instance.OpenExisting(EnvPath);
        Assert.Equal(EnvHandleKind.RegularFile, handle.Kind);
    }

    // ---------------- access ----------------

    [Fact]
    public void A_read_only_file_is_access_denied_and_untouched()
    {
        var content = Bytes("RX_VK_PACE=1\n");
        Write(content);
        File.SetAttributes(EnvPath, FileAttributes.ReadOnly);

        Assert.Equal(EnvFileOutcome.AccessDenied, Ensure());
        Assert.Equal(content, Read());
    }

    [Fact]
    public void A_file_held_open_by_another_program_is_in_use()
    {
        var content = Bytes("RX_VK_PACE=1\n");
        Write(content);
        using (new FileStream(EnvPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(EnvFileOutcome.InUse, Ensure());
        Assert.Equal(content, Read());
    }

    [Fact]
    public void The_file_is_locked_against_other_writers_while_it_is_edited()
    {
        Write(Bytes("RX_VK_PACE=1\n"));
        Exception? blocked = null;
        var fs = new ScriptedFileSystem(WindowsEnvFileSystemForTests.Instance)
        {
            BeforeAppend = () =>
            {
                try { using var _ = new FileStream(EnvPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); }
                catch (IOException ex) { blocked = ex; }
            },
        };

        Assert.Equal(EnvFileOutcome.Appended, Ensure(fs));
        Assert.NotNull(blocked); // another writer could not open it mid-edit
    }

    [Fact]
    public void Concurrent_requests_add_exactly_one_setting()
    {
        Write(Bytes("RX_VK_PACE=1\n"));
        var outcomes = new EnvFileOutcome[8];
        Parallel.For(0, outcomes.Length, i =>
        {
            // A busy file is retried, as a user would; each call is still all-or-nothing.
            // (The pause is a retry back-off, not a timing assumption: nothing is asserted on time.)
            for (var attempt = 0; attempt < 5000; attempt++)
            {
                outcomes[i] = Ensure();
                if (outcomes[i] != EnvFileOutcome.InUse)
                    break;
                Thread.Sleep(1);
            }
        });

        Assert.Single(outcomes, o => o == EnvFileOutcome.Appended);
        Assert.All(outcomes, o => Assert.Contains(o, new[] { EnvFileOutcome.Appended, EnvFileOutcome.AlreadyPresent }));
        Assert.Equal(1, Encoding.ASCII.GetString(Read()).Split(Line).Length - 1);
    }

    // ---------------- write failures and recovery ----------------

    [Fact]
    public void A_failed_append_is_rolled_back_and_verified()
    {
        var content = Bytes("# notes\nRX_VK_PACE=1\n");
        Write(content);
        var fs = new ScriptedFileSystem(WindowsEnvFileSystemForTests.Instance) { PartialAppendThenFail = true };

        Assert.Equal(EnvFileOutcome.WriteFailed, Ensure(fs));
        Assert.Equal(content, Read()); // no partial setting left behind
        Assert.Equal(1, fs.Truncations);
    }

    [Fact]
    public void A_failed_rollback_is_reported_as_recovery_failed()
    {
        var content = Bytes("# notes\nRX_VK_PACE=1\n");
        Write(content);
        var fs = new ScriptedFileSystem(WindowsEnvFileSystemForTests.Instance) { PartialAppendThenFail = true, FailTruncate = true };

        Assert.Equal(EnvFileOutcome.RecoveryFailed, Ensure(fs));
        Assert.NotEqual(content, Read()); // genuinely left partly changed: never claimed as restored
    }

    [Fact]
    public void A_rollback_that_cannot_be_verified_is_reported_as_recovery_failed()
    {
        Write(Bytes("# notes\nRX_VK_PACE=1\n"));
        var fs = new ScriptedFileSystem(WindowsEnvFileSystemForTests.Instance) { PartialAppendThenFail = true, CorruptReadAfterTruncate = true };

        Assert.Equal(EnvFileOutcome.RecoveryFailed, Ensure(fs));
    }

    [Fact]
    public void A_failed_write_to_a_new_file_removes_it()
    {
        var fs = new ScriptedFileSystem(WindowsEnvFileSystemForTests.Instance) { PartialAppendThenFail = true };

        Assert.Equal(EnvFileOutcome.WriteFailed, Ensure(fs));
        Assert.False(File.Exists(EnvPath));
    }

    [Fact]
    public void A_successful_write_is_flushed_to_disk()
    {
        Write(Bytes("RX_VK_PACE=1\n"));
        var fs = new ScriptedFileSystem(WindowsEnvFileSystemForTests.Instance);

        Assert.Equal(EnvFileOutcome.Appended, Ensure(fs));
        Assert.Equal(1, fs.DiskFlushes);
    }

    // ---------------- cancellation ----------------

    [Fact]
    public void Cancellation_before_writing_leaves_everything_untouched()
    {
        var content = Bytes("RX_VK_PACE=1\n");
        Write(content);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(EnvFileOutcome.Cancelled, Ensure(ct: cts.Token));
        Assert.Equal(content, Read());

        File.Delete(EnvPath);
        Assert.Equal(EnvFileOutcome.Cancelled, Ensure(ct: cts.Token));
        Assert.False(File.Exists(EnvPath));
    }

    [Fact]
    public void Cancellation_after_the_append_started_completes_the_write()
    {
        Write(Bytes("RX_VK_PACE=1\n"));
        using var cts = new CancellationTokenSource();
        var fs = new ScriptedFileSystem(WindowsEnvFileSystemForTests.Instance) { BeforeAppend = cts.Cancel };

        Assert.Equal(EnvFileOutcome.Appended, Ensure(fs, cts.Token));
        Assert.Equal(LongshotEnvFile.SettingValue, LongshotEnvFile.EffectiveControlValue(Read()));
    }

    // ---------------- helpers ----------------

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private static void RunCmd(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}

/// <summary>The real file system, for tests.</summary>
internal static class WindowsEnvFileSystemForTests
{
    public static IEnvFileSystem Instance => WindowsEnvFileSystem.Instance;
}

/// <summary>Wraps a file system to script races and failures around real files.</summary>
internal sealed class ScriptedFileSystem : IEnvFileSystem
{
    private readonly IEnvFileSystem _inner;
    private int _probes;

    public ScriptedFileSystem(IEnvFileSystem inner) => _inner = inner;

    /// <summary>(path, call number from 1) → a kind to report instead, or null for the real one.</summary>
    public Func<string, int, EnvPathKind?>? ProbeOverride { get; set; }
    public Action<string>? BeforeCreate { get; set; }
    public Action? BeforeAppend { get; set; }
    public EnvHandleKind? OpenedKind { get; set; }
    public bool PartialAppendThenFail { get; set; }
    public bool FailTruncate { get; set; }
    public bool CorruptReadAfterTruncate { get; set; }
    public int Appends { get; private set; }
    public int Truncations { get; private set; }
    public int DiskFlushes { get; private set; }

    public EnvPathKind Probe(string path)
    {
        var call = ++_probes;
        return ProbeOverride?.Invoke(path, call) ?? _inner.Probe(path);
    }

    /// <summary>Thrown by <see cref="OpenExisting"/> instead of opening.</summary>
    public Exception? OpenException { get; set; }

    public IEnvFileHandle OpenExisting(string path)
        => OpenException is { } ex ? throw ex : new Handle(this, _inner.OpenExisting(path));

    public IEnvFileHandle CreateNew(string path)
    {
        BeforeCreate?.Invoke(path);
        return new Handle(this, _inner.CreateNew(path));
    }

    public void Delete(string path) => _inner.Delete(path);

    private sealed class Handle : IEnvFileHandle
    {
        private readonly ScriptedFileSystem _owner;
        private readonly IEnvFileHandle _inner;
        private bool _truncated;

        public Handle(ScriptedFileSystem owner, IEnvFileHandle inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public EnvHandleKind Kind => _owner.OpenedKind ?? _inner.Kind;
        public long Length => _inner.Length;

        public byte[] ReadAll()
        {
            var bytes = _inner.ReadAll();
            if (_truncated && _owner.CorruptReadAfterTruncate && bytes.Length > 0)
                bytes[0] ^= 0xFF;
            return bytes;
        }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            _owner.Appends++;
            _owner.BeforeAppend?.Invoke();
            if (_owner.PartialAppendThenFail)
            {
                _inner.Append(bytes[..(bytes.Length / 2)]); // part of the setting reaches the file
                throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
            }
            _inner.Append(bytes);
        }

        public void FlushToDisk()
        {
            _owner.DiskFlushes++;
            _inner.FlushToDisk();
        }

        public void Truncate(long length)
        {
            _owner.Truncations++;
            if (_owner.FailTruncate)
                throw new IOException("The device is not ready.", unchecked((int)0x80070015));
            _inner.Truncate(length);
            _truncated = true;
        }

        public void Dispose() => _inner.Dispose();
    }
}
