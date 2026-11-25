using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SwitchAchievements.Core;

namespace SwitchAchievements.Longshot;

/// <summary>
/// Collects Longshot identity evidence for one already-selected game process
/// (M5.6.4): the game's program id (from <c>.longshot_exefs\main.npdm</c>) and its
/// main module's build id (from the header of <c>.longshot_exefs\main</c>), both in
/// the selected executable's own installation.
///
/// <para>This class only produces evidence. It never resolves it against the
/// catalog, decides support, connects to the bridge, caches, or touches sessions.</para>
///
/// <para><b>Partial evidence.</b> Each file yields its own fact independently: a fact
/// is included only when its own file validates, and every failure is reported as an
/// <see cref="EvidenceIssue"/>. The resolver requires both facts for identification,
/// so partial evidence can never identify a game, but keeps diagnostics such as
/// "the program is Ocarina of Time, but the build could not be read".</para>
///
/// <para><b>Path safety.</b> The installation is derived only from the selected
/// executable path, which must be a fully qualified, already-normalised
/// <c>…\bin\longshot.exe</c> path on a local volume. Device-namespace paths and
/// network locations (UNC paths and network drives) are rejected in this phase: opening
/// a file is synchronous and cannot be cancelled, so the collection deadline could not
/// be guaranteed for an unavailable share. Junctions and symbolic links are allowed.
/// Nothing read from the files is ever used to build a path.</para>
///
/// <para><b>Bounded, read-only I/O.</b> Files are opened read-only, sharing
/// read/write/delete with other processes; <c>main.npdm</c> is read only if it is at
/// most 64 KiB, and exactly the 0x100-byte NSO header is read from <c>main</c>. Reads
/// are asynchronous and cancellable; the whole collection is bounded by a total
/// deadline (default 5 seconds). The deadline cannot interrupt a blocked synchronous
/// file open. Caller cancellation propagates as <see cref="OperationCanceledException"/>;
/// the provider's own deadline is reported as an <see cref="EvidenceIssueKind.Unreadable"/>
/// issue.</para>
///
/// <para>Diagnostics are fixed messages naming only <c>main.npdm</c> / <c>main</c>,
/// offsets, and reasons — never local paths or exception text.</para>
/// </summary>
public sealed class LongshotGameIdentityProvider : IGameIdentityProvider
{
    /// <summary>Default total deadline for one collection (both files).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    internal const string ExefsDirectoryName = ".longshot_exefs";
    internal const string NpdmFileName = "main.npdm";
    internal const string NsoFileName = "main";

    private readonly ILongshotFileAccess _files;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    public LongshotGameIdentityProvider(TimeSpan? timeout = null)
        : this(LongshotFileAccess.Instance, TimeProvider.System, timeout ?? DefaultTimeout)
    {
    }

    /// <summary>Test seam: file access and time are injectable.</summary>
    internal LongshotGameIdentityProvider(ILongshotFileAccess files, TimeProvider time, TimeSpan timeout)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The collection timeout must be positive.");
        _timeout = timeout;
    }

    public async Task<GameIdentityEvidence> CollectAsync(GameProcessInfo selected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ct.ThrowIfCancellationRequested();

        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        var issues = new List<EvidenceIssue>();
        var process = new ProcessFingerprint(selected.ProcessId, selected.ProcessStartUtc);

        if (selected.ProcessId <= 0 || selected.ProcessStartUtc == default)
            issues.Add(new EvidenceIssue(EvidenceIssueKind.Other, "the selected process has no valid process fingerprint"));

        if (!TryGetExefsDirectory(selected.ExecutablePath, out var exefs, out var pathFailure))
        {
            issues.Add(new EvidenceIssue(EvidenceIssueKind.Malformed, pathFailure));
        }
        else
        {
            using var deadline = new CancellationTokenSource(_timeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

            var stage = NpdmFileName;
            try
            {
                var npdm = await ReadAsync(Path.Join(exefs, NpdmFileName), NpdmFileName, LongshotIdentityFormat.ProgramIdKey,
                    minLength: LongshotExefsParser.MetaHeaderLength, maxLength: LongshotExefsParser.MaxNpdmLength,
                    readWholeFile: true, issues, linked.Token).ConfigureAwait(false);
                if (npdm is not null)
                {
                    if (LongshotExefsParser.TryParseProgramId(npdm, out var programId, out var failure))
                        facts[LongshotIdentityFormat.ProgramIdKey] = programId;
                    else
                        issues.Add(new EvidenceIssue(EvidenceIssueKind.Malformed, failure, LongshotIdentityFormat.ProgramIdKey));
                }

                stage = NsoFileName;
                var header = await ReadAsync(Path.Join(exefs, NsoFileName), NsoFileName, LongshotIdentityFormat.MainBuildIdKey,
                    minLength: LongshotExefsParser.NsoHeaderLength, maxLength: null,
                    readWholeFile: false, issues, linked.Token).ConfigureAwait(false);
                if (header is not null)
                {
                    if (LongshotExefsParser.TryParseMainBuildId(header, out var buildId, out var failure))
                        facts[LongshotIdentityFormat.MainBuildIdKey] = buildId;
                    else
                        issues.Add(new EvidenceIssue(EvidenceIssueKind.Malformed, failure, LongshotIdentityFormat.MainBuildIdKey));
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                // The provider's own deadline (never the caller's cancellation, which propagates).
                var seconds = _timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                if (stage == NpdmFileName)
                {
                    issues.Add(new EvidenceIssue(EvidenceIssueKind.Unreadable,
                        $"identity collection timed out after {seconds} s while reading main.npdm", LongshotIdentityFormat.ProgramIdKey));
                    issues.Add(new EvidenceIssue(EvidenceIssueKind.Unreadable,
                        "main was not read because identity collection timed out", LongshotIdentityFormat.MainBuildIdKey));
                }
                else
                {
                    issues.Add(new EvidenceIssue(EvidenceIssueKind.Unreadable,
                        $"identity collection timed out after {seconds} s while reading main", LongshotIdentityFormat.MainBuildIdKey));
                }
            }
        }

        return new GameIdentityEvidence(LongshotIdentityFormat.Integration, process, facts, issues, _time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Opens a file read-only and reads either the whole file (bounded by
    /// <paramref name="maxLength"/>) or exactly <paramref name="minLength"/> bytes.
    /// Expected file-system failures become issues and return null; cancellation and
    /// unexpected exceptions propagate.
    /// </summary>
    private async Task<byte[]?> ReadAsync(
        string path,
        string fileName,
        string factKey,
        int minLength,
        int? maxLength,
        bool readWholeFile,
        List<EvidenceIssue> issues,
        CancellationToken token)
    {
        try
        {
            await using var stream = _files.OpenRead(path);
            var length = stream.Length; // from the opened handle; no separate existence check

            if (length < minLength)
            {
                issues.Add(new EvidenceIssue(EvidenceIssueKind.Malformed,
                    $"{fileName} is shorter than the expected 0x{minLength:x} bytes", factKey));
                return null;
            }

            if (maxLength is int max && length > max)
            {
                issues.Add(new EvidenceIssue(EvidenceIssueKind.Malformed,
                    $"{fileName} exceeds the {max}-byte size limit", factKey));
                return null;
            }

            // Bounded: at most MaxNpdmLength for the whole-file read, otherwise the fixed header.
            var buffer = new byte[readWholeFile ? (int)length : minLength];
            await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
            return buffer;
        }
        catch (FileNotFoundException)
        {
            issues.Add(new EvidenceIssue(EvidenceIssueKind.Missing, $"{fileName} was not found", factKey));
        }
        catch (DirectoryNotFoundException)
        {
            issues.Add(new EvidenceIssue(EvidenceIssueKind.Missing, $"the {ExefsDirectoryName} folder containing {fileName} was not found", factKey));
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new EvidenceIssue(EvidenceIssueKind.Unreadable, $"access to {fileName} was denied", factKey));
        }
        catch (EndOfStreamException)
        {
            issues.Add(new EvidenceIssue(EvidenceIssueKind.Malformed, $"{fileName} ended early (truncated or changed while reading)", factKey));
        }
        catch (IOException)
        {
            // Sharing violations, path-too-long, device and other I/O errors.
            issues.Add(new EvidenceIssue(EvidenceIssueKind.Unreadable, $"{fileName} could not be read (I/O error)", factKey));
        }

        return null;
    }

    /// <summary>
    /// Validates the selected executable path and derives
    /// <c>&lt;game&gt;\.longshot_exefs</c> from it. Fixed failure reasons only.
    /// </summary>
    internal bool TryGetExefsDirectory(string? executablePath, out string exefsDirectory, out string failure)
    {
        // M5.8.2: the validation is shared with live-tracking setup (LongshotGameFolder);
        // behaviour and failure reasons are unchanged.
        exefsDirectory = string.Empty;
        if (!LongshotGameFolder.TryGetGameDirectory(executablePath, _files.IsOnNetworkDrive, out var gameDirectory, out failure))
            return false;

        exefsDirectory = Path.Join(gameDirectory, ExefsDirectoryName);
        return true;
    }
}

/// <summary>Read-only file access used by <see cref="LongshotGameIdentityProvider"/> (internal test seam).</summary>
internal interface ILongshotFileAccess
{
    /// <summary>Opens an existing file for reading only; throws the usual file-system exceptions.</summary>
    Stream OpenRead(string path);

    /// <summary>True when the fully qualified path is on a network drive.</summary>
    bool IsOnNetworkDrive(string fullyQualifiedPath);
}

/// <summary>The real, read-only file access.</summary>
internal sealed class LongshotFileAccess : ILongshotFileAccess
{
    public static readonly LongshotFileAccess Instance = new();

    private LongshotFileAccess()
    {
    }

    public Stream OpenRead(string path) => new FileStream(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.Asynchronous,
        BufferSize = 0,
    });

    public bool IsOnNetworkDrive(string fullyQualifiedPath)
    {
        var root = Path.GetPathRoot(fullyQualifiedPath);
        if (string.IsNullOrEmpty(root))
            return false;

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false; // not a drive-letter root; UNC forms were rejected earlier
        }
    }
}
