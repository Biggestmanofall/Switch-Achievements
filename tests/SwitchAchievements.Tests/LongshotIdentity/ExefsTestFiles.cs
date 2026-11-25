using System.Buffers.Binary;
using System.Text;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.Tests.LongshotIdentity;

/// <summary>Synthetic NPDM / NSO bytes laid out exactly like Longshot v3.0.0's files.</summary>
internal static class ExefsBytes
{
    public const ulong OotProgramId = 0x0400486024DEC000;
    public const string OotProgramIdHex = "0400486024dec000";
    public const string OotBuildIdHex = "e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348";
    public static byte[] OotBuildId => Convert.FromHexString(OotBuildIdHex);

    // The real file: META header 0x80, ACID 0x80..0x554, ACI0 at 0x560 (size 0x1C4), total 0x724.
    public const uint RealAci0Offset = 0x560;
    public const uint RealAci0Size = 0x1C4;

    public static byte[] Npdm(
        ulong programId = OotProgramId,
        uint aci0Offset = RealAci0Offset,
        uint aci0Size = RealAci0Size,
        int? length = null,
        string metaMagic = "META",
        string aci0Magic = "ACI0")
    {
        var total = length ?? (int)Math.Min((ulong)aci0Offset + aci0Size, 128 * 1024);
        var bytes = new byte[total];
        Put(bytes, 0, Encoding.ASCII.GetBytes(metaMagic));
        if (total >= 0x78)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x70), aci0Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x74), aci0Size);
        }

        if ((ulong)aci0Offset + 0x18 <= (ulong)total)
        {
            Put(bytes, (int)aci0Offset, Encoding.ASCII.GetBytes(aci0Magic));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan((int)aci0Offset + 0x10), programId);
        }

        return bytes;
    }

    public static byte[] NsoHeader(
        byte[]? buildId20 = null,
        byte[]? padding12 = null,
        uint version = 0,
        string magic = "NSO0",
        int length = 0x100)
    {
        var bytes = new byte[length];
        Put(bytes, 0, Encoding.ASCII.GetBytes(magic));
        if (length >= 8)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), version);
        Put(bytes, 0x40, buildId20 ?? OotBuildId);
        Put(bytes, 0x54, padding12 ?? new byte[12]);
        return bytes;
    }

    private static void Put(byte[] target, int offset, byte[] source)
    {
        for (var i = 0; i < source.Length && offset + i < target.Length; i++)
            target[offset + i] = source[i];
    }
}

/// <summary>
/// A temporary Longshot-shaped installation:
/// <c>&lt;root&gt;\Longshot\bin\longshot.exe</c> plus <c>&lt;root&gt;\Longshot\.longshot_exefs\{main.npdm,main}</c>.
/// </summary>
internal sealed class TempGame : IDisposable
{
    public TempGame(byte[]? npdm, byte[]? nso, bool createExefs = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "sa-longshot-identity-" + Guid.NewGuid().ToString("N"));
        GameDirectory = Path.Combine(Root, "Longshot");
        var bin = Path.Combine(GameDirectory, "bin");
        Directory.CreateDirectory(bin);
        ExecutablePath = Path.Combine(bin, "longshot.exe");
        File.WriteAllBytes(ExecutablePath, Array.Empty<byte>());

        ExefsDirectory = Path.Combine(GameDirectory, ".longshot_exefs");
        NpdmPath = Path.Combine(ExefsDirectory, "main.npdm");
        NsoPath = Path.Combine(ExefsDirectory, "main");
        if (createExefs)
        {
            Directory.CreateDirectory(ExefsDirectory);
            if (npdm is not null)
                File.WriteAllBytes(NpdmPath, npdm);
            if (nso is not null)
                File.WriteAllBytes(NsoPath, nso);
        }
    }

    public static TempGame Valid() => new(ExefsBytes.Npdm(), WithBody(ExefsBytes.NsoHeader()));

    /// <summary>An NSO header followed by some module bytes, like a real (much larger) file.</summary>
    public static byte[] WithBody(byte[] header) => header.Concat(Enumerable.Repeat((byte)0xCD, 4096)).ToArray();

    public string Root { get; }
    public string GameDirectory { get; }
    public string ExecutablePath { get; }
    public string ExefsDirectory { get; }
    public string NpdmPath { get; }
    public string NsoPath { get; }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Scriptable file access: each file name maps to a stream factory (which may throw).</summary>
internal sealed class ScriptedFileAccess : ILongshotFileAccess
{
    private readonly Dictionary<string, Func<Stream>> _files = new(StringComparer.Ordinal);
    private readonly List<string> _opened = new();

    public bool NetworkDrive { get; set; }

    public IReadOnlyList<string> Opened
    {
        get { lock (_opened) return _opened.ToArray(); }
    }

    public ScriptedFileAccess Npdm(Func<Stream> open) { _files["main.npdm"] = open; return this; }
    public ScriptedFileAccess Nso(Func<Stream> open) { _files["main"] = open; return this; }

    public Stream OpenRead(string path)
    {
        var name = Path.GetFileName(path);
        lock (_opened)
            _opened.Add(name);
        return _files.TryGetValue(name, out var open) ? open() : throw new FileNotFoundException("scripted: not found");
    }

    public bool IsOnNetworkDrive(string fullyQualifiedPath) => NetworkDrive;
}

/// <summary>Base for test streams: seekable, read-only, with an arbitrary reported length.</summary>
internal abstract class TestStream : Stream
{
    protected TestStream(long length) => ReportedLength = length;

    public long ReportedLength { get; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => ReportedLength;
    public override long Position { get; set; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Serves <c>data</c>, then ends early although it reports a larger length.</summary>
internal sealed class TruncatedStream : TestStream
{
    private readonly byte[] _data;

    public TruncatedStream(byte[] data, long reportedLength) : base(reportedLength) => _data = data;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var available = (int)Math.Max(0, Math.Min(count, _data.Length - Position));
        Array.Copy(_data, Position, buffer, offset, available);
        Position += available;
        return available;
    }
}

/// <summary>Reports a huge length but serves a valid header; records how much was read.</summary>
internal sealed class HugeFileStream : TestStream
{
    private readonly byte[] _header;

    public HugeFileStream(byte[] header, long reportedLength) : base(reportedLength) => _header = header;

    public long TotalBytesRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++)
            buffer[offset + i] = Position + i < _header.Length ? _header[Position + i] : (byte)0xAB;
        Position += count;
        TotalBytesRead += count;
        return count;
    }
}

/// <summary>
/// A read that blocks until its cancellation token is cancelled. <see cref="ReadStarted"/>
/// completes when a read begins; <see cref="ReadEnded"/> when that read has finished.
/// No sleeps: the read waits only on the token.
/// </summary>
internal sealed class BlockingStream : TestStream
{
    public BlockingStream(long length) : base(length) { }

    public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReadEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadStarted.TrySetResult();
        try
        {
            await new TaskCompletionSource().Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            ReadEnded.TrySetResult();
        }
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> whose clock and timers move only when the test says so,
/// making the provider's deadline deterministic.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();

    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 24, 6, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime);
        lock (_timers)
            _timers.Add(timer);
        return timer;
    }

    /// <summary>Due times requested by timers that are still active.</summary>
    public IReadOnlyList<TimeSpan> ActiveDueTimes
    {
        get { lock (_timers) return _timers.Where(t => !t.Disposed).Select(t => t.DueTime).ToArray(); }
    }

    /// <summary>Fires every active timer once (as if its due time had passed).</summary>
    public void FireAll()
    {
        ManualTimer[] due;
        lock (_timers)
            due = _timers.Where(t => !t.Disposed && t.DueTime != Timeout.InfiniteTimeSpan).ToArray();
        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
        }

        public TimeSpan DueTime { get; private set; }
        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return !Disposed;
        }

        public void Fire() => _callback(_state);

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
