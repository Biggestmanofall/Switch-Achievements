using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwitchAchievements.Longshot;

/// <summary>Outcome of <see cref="LongshotEnvFile.EnsureControlSetting"/>.</summary>
internal enum EnvFileOutcome
{
    /// <summary>The file did not exist and was created with the setting.</summary>
    Created,

    /// <summary>The setting was appended to the existing file.</summary>
    Appended,

    /// <summary>The file's effective HOS_CONTROL is already longshot_control; nothing was written.</summary>
    AlreadyPresent,

    /// <summary>The file's effective HOS_CONTROL is another value (including empty); nothing was written.</summary>
    Conflict,

    /// <summary>The file cannot be edited safely (link, directory, hard links, too large, UTF-16, NUL, long line, unverifiable); nothing was written.</summary>
    Unsupported,

    AccessDenied,

    /// <summary>Another program holds the file open, or it kept changing while being created; nothing was written.</summary>
    InUse,

    /// <summary>The file could not be read; nothing was written.</summary>
    ReadFailed,

    /// <summary>Writing failed and the file was verified to be exactly as it was before.</summary>
    WriteFailed,

    /// <summary>Writing failed and the file could NOT be verified as restored: it may be partially changed.</summary>
    RecoveryFailed,

    /// <summary>Cancelled before anything was written.</summary>
    Cancelled,
}

/// <summary>What an opened handle refers to, established from the handle itself.</summary>
internal enum EnvHandleKind
{
    RegularFile,
    ReparsePoint,
    Directory,

    /// <summary>A regular file with more than one hard link (writing would change another name too).</summary>
    MultipleLinks,

    /// <summary>The kind could not be established: never written to.</summary>
    Unknown,
}

/// <summary>What a path currently names, without following links.</summary>
internal enum EnvPathKind
{
    Missing,
    File,
    Directory,
    ReparsePoint,
}

/// <summary>An exclusively opened file (internal test seam).</summary>
internal interface IEnvFileHandle : IDisposable
{
    EnvHandleKind Kind { get; }
    long Length { get; }

    /// <summary>All bytes from the start.</summary>
    byte[] ReadAll();

    /// <summary>Writes at the end of the file.</summary>
    void Append(ReadOnlySpan<byte> bytes);

    /// <summary>Flushes to the device, not only the OS cache.</summary>
    void FlushToDisk();

    void Truncate(long length);
}

/// <summary>The file-system operations <see cref="LongshotEnvFile"/> needs (internal test seam).</summary>
internal interface IEnvFileSystem
{
    EnvPathKind Probe(string path);

    /// <summary>Opens an existing entry read/write with no sharing, without following a link. Throws <see cref="IOException"/> (HResult carries the Win32 error).</summary>
    IEnvFileHandle OpenExisting(string path);

    /// <summary>Creates a new file (fails if any entry exists), read/write with no sharing. Throws as <see cref="OpenExisting"/>.</summary>
    IEnvFileHandle CreateNew(string path);

    void Delete(string path);
}

/// <summary>
/// Ensures a Longshot game's <c>longshot_env.txt</c> sets <c>HOS_CONTROL=longshot_control</c>
/// (M5.8.2), the only setting it ever adds. Longshot reads that file at startup, before it
/// decides whether to start its control bridge (<c>host/hos_main.c</c> <c>load_longshot_env</c>).
///
/// <para>Safety:</para>
/// <list type="bullet">
/// <item>Existing files are opened without following links and with no sharing, so nothing
/// else can change them between reading and appending; the opened handle must be a regular
/// file with a single link.</item>
/// <item>Existing bytes are never rewritten: only ASCII lines are appended, in the file's own
/// line-ending style, so encoding, BOM, comments and other settings are untouched.</item>
/// <item>A file whose effective <c>HOS_CONTROL</c> is already correct is not written; any other
/// effective value is a conflict and is never overwritten.</item>
/// <item>A missing file is created with create-new semantics, so a concurrently created
/// file is never overwritten.</item>
/// <item>A failed append is rolled back to the original length and verified byte for byte;
/// if that cannot be verified the outcome is <see cref="EnvFileOutcome.RecoveryFailed"/>.</item>
/// </list>
/// </summary>
internal sealed class LongshotEnvFile
{
    public const string FileName = "longshot_env.txt";
    public const string SettingName = "HOS_CONTROL";
    public const string SettingValue = "longshot_control";

    /// <summary>Largest file this helper will edit.</summary>
    internal const int MaxFileBytes = 64 * 1024;

    /// <summary>Longshot reads lines with <c>fgets(line, 512, f)</c>: a line and its terminator must fit in 511 bytes.</summary>
    internal const int MaxLineBytesWithTerminator = 511;

    internal const string Comment = "# Added by Switch Achievements: lets it read live gameplay data through Longshot's control bridge.";

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private readonly IEnvFileSystem _fs;

    public LongshotEnvFile(IEnvFileSystem? fileSystem = null) => _fs = fileSystem ?? WindowsEnvFileSystem.Instance;

    /// <summary>The bytes of a newly created file: UTF-8 without BOM (ASCII), Windows line endings.</summary>
    internal static byte[] NewFileBytes => Encoding.ASCII.GetBytes(Comment + "\r\n" + SettingName + "=" + SettingValue + "\r\n");

    /// <param name="gameDirectory">The validated game folder (<see cref="LongshotGameFolder"/>).</param>
    /// <param name="ct">Honoured until the write begins; a started write is completed or rolled back.</param>
    public EnvFileOutcome EnsureControlSetting(string gameDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDirectory);
        var path = Path.Join(gameDirectory, FileName);

        // Two passes at most: a file that appears or disappears between the probe and the
        // open is handled by looking again once; a file that keeps changing is "in use".
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (ct.IsCancellationRequested)
                return EnvFileOutcome.Cancelled;

            EnvPathKind kind;
            try
            {
                kind = _fs.Probe(path);
            }
            catch (UnauthorizedAccessException)
            {
                return EnvFileOutcome.AccessDenied;
            }
            catch (IOException)
            {
                return EnvFileOutcome.ReadFailed;
            }

            switch (kind)
            {
                case EnvPathKind.Directory:
                case EnvPathKind.ReparsePoint:
                    return EnvFileOutcome.Unsupported;

                case EnvPathKind.Missing:
                    var created = TryCreate(path, ct);
                    if (created is { } createdOutcome)
                        return createdOutcome;
                    continue; // something was created meanwhile: edit it as an existing file

                default:
                    var edited = TryEditExisting(path, ct);
                    if (edited is { } editedOutcome)
                        return editedOutcome;
                    continue; // it disappeared meanwhile: create it
            }
        }

        return EnvFileOutcome.InUse;
    }

    /// <summary>Null when an entry already exists (created concurrently).</summary>
    private EnvFileOutcome? TryCreate(string path, CancellationToken ct)
    {
        IEnvFileHandle handle;
        try
        {
            handle = _fs.CreateNew(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = Win32Error(ex);
            if (error is ErrorFileExists or ErrorAlreadyExists)
                return null;
            return MapOpenFailure(ex, error, EnvFileOutcome.WriteFailed); // nothing was created
        }

        using (handle)
        {
            if (handle.Kind != EnvHandleKind.RegularFile)
            {
                // Not what was asked for: remove what was just made, and touch nothing else.
                handle.Dispose();
                return TryDelete(path) ? EnvFileOutcome.Unsupported : EnvFileOutcome.RecoveryFailed;
            }

            if (ct.IsCancellationRequested)
            {
                handle.Dispose();
                return TryDelete(path) ? EnvFileOutcome.Cancelled : EnvFileOutcome.RecoveryFailed;
            }

            try
            {
                handle.Append(NewFileBytes);
                handle.FlushToDisk();
                return EnvFileOutcome.Created;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Remove the file just created rather than leave a partial one.
                handle.Dispose();
                return TryDelete(path) ? EnvFileOutcome.WriteFailed : EnvFileOutcome.RecoveryFailed;
            }
        }
    }

    /// <summary>Null when the file disappeared before it could be opened.</summary>
    private EnvFileOutcome? TryEditExisting(string path, CancellationToken ct)
    {
        IEnvFileHandle handle;
        try
        {
            handle = _fs.OpenExisting(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = Win32Error(ex);
            if (error is ErrorFileNotFound or ErrorPathNotFound)
                return null;
            return MapOpenFailure(ex, error, EnvFileOutcome.ReadFailed);
        }

        using (handle)
        {
            // The handle itself, not the earlier path check, decides what is being edited.
            if (handle.Kind != EnvHandleKind.RegularFile)
                return EnvFileOutcome.Unsupported;

            byte[] original;
            try
            {
                if (handle.Length > MaxFileBytes)
                    return EnvFileOutcome.Unsupported;
                original = handle.ReadAll();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return EnvFileOutcome.ReadFailed;
            }

            if (original.Length > MaxFileBytes || !IsSupportedContent(original))
                return EnvFileOutcome.Unsupported;

            var effective = EffectiveControlValue(original);
            if (effective is not null)
                return effective == SettingValue ? EnvFileOutcome.AlreadyPresent : EnvFileOutcome.Conflict;

            if (ct.IsCancellationRequested)
                return EnvFileOutcome.Cancelled;

            var addition = AppendBytes(original);
            try
            {
                handle.Append(addition);
                handle.FlushToDisk();
                return EnvFileOutcome.Appended;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return RollBack(handle, original);
            }
        }
    }

    /// <summary>Restores the original length and verifies the bytes; RecoveryFailed unless verified.</summary>
    private static EnvFileOutcome RollBack(IEnvFileHandle handle, byte[] original)
    {
        try
        {
            handle.Truncate(original.Length);
            handle.FlushToDisk();
            var now = handle.ReadAll();
            return now.AsSpan().SequenceEqual(original) ? EnvFileOutcome.WriteFailed : EnvFileOutcome.RecoveryFailed;
        }
        catch (Exception)
        {
            return EnvFileOutcome.RecoveryFailed;
        }
    }

    private bool TryDelete(string path)
    {
        try
        {
            _fs.Delete(path);
            return _fs.Probe(path) == EnvPathKind.Missing;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------- content (Longshot's parser semantics) ----------------

    /// <summary>
    /// Rejects what this helper will not edit: UTF-16 (BOM), NUL bytes (also BOM-less
    /// UTF-16), and lines Longshot would split (longer than its 512-byte read buffer).
    /// </summary>
    internal static bool IsSupportedContent(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 2 && ((content[0] == 0xFF && content[1] == 0xFE) || (content[0] == 0xFE && content[1] == 0xFF)))
            return false;
        if (content.IndexOf((byte)0) >= 0)
            return false;

        var start = 0;
        while (start < content.Length)
        {
            var newline = content[start..].IndexOf((byte)'\n');
            var lineWithTerminator = newline < 0 ? content.Length - start : newline + 1;
            if (lineWithTerminator > MaxLineBytesWithTerminator)
                return false;
            start += lineWithTerminator;
        }
        return true;
    }

    /// <summary>
    /// The value Longshot would take for HOS_CONTROL from this file, or null when no line
    /// sets it. Mirrors <c>load_longshot_env</c> and <c>env_line_allowed</c>: a UTF-8 BOM is
    /// skipped; leading spaces and tabs and trailing whitespace are trimmed; empty, <c>#</c>
    /// and <c>=</c>-less lines are ignored; the name is exactly the text before the first
    /// <c>=</c>; a value Longshot leaves out (a path, a drive, <c>~</c>, <c>.</c>, <c>..</c>)
    /// is ignored; the last line taken wins.
    /// </summary>
    internal static string? EffectiveControlValue(ReadOnlySpan<byte> content)
    {
        string? effective = null;
        var start = 0;
        while (start < content.Length)
        {
            var newline = content[start..].IndexOf((byte)'\n');
            var line = newline < 0 ? content[start..] : content.Slice(start, newline);
            start += newline < 0 ? line.Length : newline + 1;

            if (line.StartsWith(Utf8Bom))
                line = line[3..];
            while (line.Length > 0 && (line[0] == (byte)' ' || line[0] == (byte)'\t'))
                line = line[1..];
            while (line.Length > 0 && line[^1] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
                line = line[..^1];
            if (line.Length == 0 || line[0] == (byte)'#')
                continue;

            var eq = line.IndexOf((byte)'=');
            if (eq < 0)
                continue;

            var name = Encoding.Latin1.GetString(line[..eq]);
            if (!string.Equals(name, SettingName, StringComparison.Ordinal))
                continue;

            var value = Encoding.Latin1.GetString(line[(eq + 1)..]);
            if (LongshotLeavesOut(value))
                continue;

            effective = value;
        }
        return effective;
    }

    /// <summary>env_line_allowed's value rule for a non-file switch: no path separators, drive, <c>~</c>, <c>.</c> or <c>..</c>.</summary>
    private static bool LongshotLeavesOut(string value)
    {
        if (value.IndexOfAny(new[] { '/', '\\' }) >= 0)
            return true;
        var drive = value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';
        return drive || value.StartsWith('~') || value == "." || value == "..";
    }

    /// <summary>The comment and setting lines, in the file's line-ending style, after a line break if the file lacks a final one.</summary>
    internal static byte[] AppendBytes(ReadOnlySpan<byte> original)
    {
        var firstNewline = original.IndexOf((byte)'\n');
        var eol = firstNewline > 0 && original[firstNewline - 1] == (byte)'\r' ? "\r\n"
            : firstNewline >= 0 ? "\n"
            : "\r\n";

        var text = new StringBuilder();
        if (original.Length > 0 && original[^1] != (byte)'\n')
            text.Append(eol);
        text.Append(Comment).Append(eol);
        text.Append(SettingName).Append('=').Append(SettingValue).Append(eol);
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    // ---------------- error mapping ----------------

    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorWriteProtect = 19;
    internal const int ErrorSharingViolation = 32;
    internal const int ErrorLockViolation = 33;
    internal const int ErrorFileExists = 80;
    internal const int ErrorAlreadyExists = 183;

    private static int Win32Error(Exception ex) => ex.HResult & 0xFFFF;

    private static EnvFileOutcome MapOpenFailure(Exception ex, int error, EnvFileOutcome otherwise) => ex switch
    {
        UnauthorizedAccessException => EnvFileOutcome.AccessDenied,
        _ when error is ErrorAccessDenied or ErrorWriteProtect => EnvFileOutcome.AccessDenied,
        _ when error is ErrorSharingViolation or ErrorLockViolation => EnvFileOutcome.InUse,
        _ => otherwise,
    };
}

/// <summary>
/// The real file system for <see cref="LongshotEnvFile"/>: <c>CreateFileW</c> with
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> (a link is opened itself, never followed) and no
/// sharing, and the handle's own attributes, reparse tag, type and link count.
/// </summary>
internal sealed class WindowsEnvFileSystem : IEnvFileSystem
{
    public static readonly WindowsEnvFileSystem Instance = new();

    private WindowsEnvFileSystem()
    {
    }

    public EnvPathKind Probe(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path); // the entry's own attributes (links are not followed)
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return EnvPathKind.Missing;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return EnvPathKind.ReparsePoint;
        return (attributes & FileAttributes.Directory) != 0 ? EnvPathKind.Directory : EnvPathKind.File;
    }

    public IEnvFileHandle OpenExisting(string path) => Open(path, Native.OpenExisting);

    public IEnvFileHandle CreateNew(string path) => Open(path, Native.CreateNew);

    public void Delete(string path) => File.Delete(path);

    private static IEnvFileHandle Open(string path, uint disposition)
    {
        var handle = Native.CreateFileW(path, Native.GenericRead | Native.GenericWrite, 0 /* no sharing */, IntPtr.Zero,
            disposition, Native.FileAttributeNormal | Native.FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(new Win32Exception(error).Message, unchecked((int)0x80070000) | error);
        }

        return new Handle(handle);
    }

    private sealed class Handle : IEnvFileHandle
    {
        private readonly FileStream _stream;

        public Handle(SafeFileHandle handle)
        {
            Kind = KindOf(handle);
            _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0);
        }

        public EnvHandleKind Kind { get; }

        public long Length => _stream.Length;

        public byte[] ReadAll()
        {
            _stream.Position = 0;
            var buffer = new byte[_stream.Length];
            _stream.ReadExactly(buffer);
            return buffer;
        }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            _stream.Seek(0, SeekOrigin.End);
            _stream.Write(bytes);
        }

        public void FlushToDisk() => _stream.Flush(flushToDisk: true);

        public void Truncate(long length) => _stream.SetLength(length);

        public void Dispose() => _stream.Dispose();

        private static EnvHandleKind KindOf(SafeFileHandle handle)
        {
            if (Native.GetFileType(handle) != Native.FileTypeDisk)
                return EnvHandleKind.Unknown;

            if (!Native.GetFileInformationByHandleEx(handle, Native.FileAttributeTagInfoClass, out Native.FileAttributeTagInfo tag,
                    (uint)Marshal.SizeOf<Native.FileAttributeTagInfo>()))
                return EnvHandleKind.Unknown;
            var attributes = (FileAttributes)tag.FileAttributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return EnvHandleKind.ReparsePoint;
            if ((attributes & FileAttributes.Directory) != 0)
                return EnvHandleKind.Directory;

            if (!Native.GetFileInformationByHandle(handle, out var info))
                return EnvHandleKind.Unknown;
            return info.NumberOfLinks == 1 ? EnvHandleKind.RegularFile : EnvHandleKind.MultipleLinks;
        }
    }

    private static class Native
    {
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint CreateNew = 1;
        public const uint OpenExisting = 3;
        public const uint FileAttributeNormal = 0x80;
        public const uint FileFlagOpenReparsePoint = 0x00200000;
        public const uint FileTypeDisk = 1;
        public const int FileAttributeTagInfoClass = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct FileAttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow, CreationTimeHigh;     // FILETIMEs: two DWORDs each (4-byte aligned)
            public uint LastAccessTimeLow, LastAccessTimeHigh;
            public uint LastWriteTimeLow, LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll")]
        public static extern uint GetFileType(SafeFileHandle handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileAttributeTagInfo info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
    }
}
