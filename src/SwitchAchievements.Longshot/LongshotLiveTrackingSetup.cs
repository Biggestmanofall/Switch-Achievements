using System.IO;
using SwitchAchievements.Core;

namespace SwitchAchievements.Longshot;

/// <summary>The result of <see cref="LongshotLiveTrackingSetup.Enable"/> (M5.8.2).</summary>
public enum LiveTrackingSetupResult
{
    /// <summary><c>HOS_CONTROL=longshot_control</c> was written to the game's longshot_env.txt.</summary>
    Enabled,

    /// <summary>longshot_env.txt already sets it; nothing was written.</summary>
    AlreadyEnabled,

    /// <summary>A persistent Windows environment variable already sets it; nothing was written.</summary>
    AlreadyEnabledByEnvironment,

    /// <summary>A persistent HOS_CONTROL (User or Machine) has another value, or the two disagree; it would take priority, so nothing was written.</summary>
    EnvironmentConflict,

    /// <summary>The persistent environment could not be read, so precedence is unknown; nothing was written.</summary>
    EnvironmentUnreadable,

    /// <summary>longshot_env.txt already sets HOS_CONTROL to another value (including empty); it was not changed.</summary>
    FileConflict,

    /// <summary>The game's folder could not be validated (path, layout, network drive, or no image.bin).</summary>
    GameFolderUnavailable,

    /// <summary>The game is not an identified game of this integration.</summary>
    NotEligible,

    /// <summary>The identified process is no longer the uniquely selected game process.</summary>
    ProcessChanged,

    /// <summary>longshot_env.txt cannot be edited safely (link, directory, hard links, size, UTF-16, NUL, long line).</summary>
    FileUnsupported,

    AccessDenied,
    FileInUse,

    /// <summary>longshot_env.txt could not be read; nothing was written.</summary>
    ReadFailed,

    /// <summary>Writing failed; the file was verified to be unchanged.</summary>
    WriteFailed,

    /// <summary>Writing failed and the file could not be verified as restored: it may be partially changed.</summary>
    RecoveryFailed,

    /// <summary>Cancelled before anything was written.</summary>
    Cancelled,
}

/// <summary>Read-only access to the persistent (User and Machine) environment (test seam).</summary>
public interface IPersistentEnvironment
{
    string? Read(string name, EnvironmentVariableTarget target);
}

/// <summary>The real persistent environment (read from the registry; never written).</summary>
public sealed class WindowsPersistentEnvironment : IPersistentEnvironment
{
    public string? Read(string name, EnvironmentVariableTarget target) => Environment.GetEnvironmentVariable(name, target);
}

/// <summary>
/// Enables Longshot's control bridge for future launches of an identified game (M5.8.2),
/// by ensuring its <c>longshot_env.txt</c> sets <c>HOS_CONTROL=longshot_control</c>. Called
/// only after the user explicitly confirmed. Never shows UI, never touches the environment,
/// never starts or stops a process, and takes no monitoring locks.
///
/// <para>Steps: eligibility (an <see cref="IdentifiedGame"/> of this integration); the game
/// process re-selected with the monitoring pipeline and required to be the identified
/// process (PID and start time); its folder validated (<see cref="LongshotGameFolder"/>);
/// persistent environment precedence checked; the process re-selected once more right
/// before writing; then <see cref="LongshotEnvFile"/>.</para>
///
/// <para>Limitation: a HOS_CONTROL given only to one launch (by a launcher) cannot be seen
/// here, and another process's environment is never inspected. It would take priority over
/// the file; the dashboard's diagnostic then stays, truthfully.</para>
/// </summary>
public sealed class LongshotLiveTrackingSetup
{
    private readonly IGameIntegration _integration;
    private readonly IProcessProvider _processes;
    private readonly Action<string>? _log;
    private readonly IPersistentEnvironment _environment;
    private readonly LongshotEnvFile _file;
    private readonly Func<string, bool> _isOnNetworkDrive;
    private readonly Func<string, bool> _fileExists;
    private readonly object _gate = new();

    /// <param name="integration">Reduces processes to game candidates (the Longshot integration excludes crash helpers).</param>
    /// <param name="processProvider">Lists processes.</param>
    /// <param name="log">Receives fixed diagnostic lines (no paths).</param>
    public LongshotLiveTrackingSetup(IGameIntegration integration, IProcessProvider processProvider, Action<string>? log = null)
        : this(integration, processProvider, log, new WindowsPersistentEnvironment(), new LongshotEnvFile(),
            LongshotFileAccess.Instance.IsOnNetworkDrive, File.Exists)
    {
    }

    internal LongshotLiveTrackingSetup(
        IGameIntegration integration,
        IProcessProvider processProvider,
        Action<string>? log,
        IPersistentEnvironment environment,
        LongshotEnvFile file,
        Func<string, bool> isOnNetworkDrive,
        Func<string, bool> fileExists)
    {
        _integration = integration ?? throw new ArgumentNullException(nameof(integration));
        _processes = processProvider ?? throw new ArgumentNullException(nameof(processProvider));
        _log = log;
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _isOnNetworkDrive = isOnNetworkDrive ?? throw new ArgumentNullException(nameof(isOnNetworkDrive));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }

    /// <summary>Runs the setup for <paramref name="game"/>. Blocking (disk I/O): call off the UI thread. Concurrent calls are serialized.</summary>
    public LiveTrackingSetupResult Enable(IdentifiedGame game, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
            return EnableCore(game, ct);
    }

    private LiveTrackingSetupResult EnableCore(IdentifiedGame game, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return LiveTrackingSetupResult.Cancelled;

        if (!string.Equals(game.IntegrationId, LongshotIdentityFormat.Integration, StringComparison.Ordinal))
            return LiveTrackingSetupResult.NotEligible;

        if (SelectedProcessFor(game) is not { } selected)
            return LiveTrackingSetupResult.ProcessChanged;

        if (!LongshotGameFolder.TryResolveInstallation(selected.ExecutablePath, _isOnNetworkDrive, _fileExists, out var gameDirectory, out var failure))
        {
            _log?.Invoke("live tracking setup: the game folder is unavailable: " + failure);
            return LiveTrackingSetupResult.GameFolderUnavailable;
        }

        if (CheckPersistentEnvironment() is { } environmentResult)
            return environmentResult;

        if (ct.IsCancellationRequested)
            return LiveTrackingSetupResult.Cancelled;

        // Immediately before writing: still the same process, from the same executable.
        if (SelectedProcessFor(game) is not { } again
            || !string.Equals(again.ExecutablePath, selected.ExecutablePath, StringComparison.Ordinal))
            return LiveTrackingSetupResult.ProcessChanged;

        return _file.EnsureControlSetting(gameDirectory, ct) switch
        {
            EnvFileOutcome.Created or EnvFileOutcome.Appended => LiveTrackingSetupResult.Enabled,
            EnvFileOutcome.AlreadyPresent => LiveTrackingSetupResult.AlreadyEnabled,
            EnvFileOutcome.Conflict => LiveTrackingSetupResult.FileConflict,
            EnvFileOutcome.Unsupported => LiveTrackingSetupResult.FileUnsupported,
            EnvFileOutcome.AccessDenied => LiveTrackingSetupResult.AccessDenied,
            EnvFileOutcome.InUse => LiveTrackingSetupResult.FileInUse,
            EnvFileOutcome.ReadFailed => LiveTrackingSetupResult.ReadFailed,
            EnvFileOutcome.WriteFailed => LiveTrackingSetupResult.WriteFailed,
            EnvFileOutcome.RecoveryFailed => LiveTrackingSetupResult.RecoveryFailed,
            EnvFileOutcome.Cancelled => LiveTrackingSetupResult.Cancelled,
            _ => LiveTrackingSetupResult.WriteFailed,
        };
    }

    /// <summary>
    /// The monitoring pipeline's selection (list, integration candidates, selection policy):
    /// the process only when it is the unique selection and exactly the identified process.
    /// </summary>
    private GameProcessInfo? SelectedProcessFor(IdentifiedGame game)
    {
        ProcessSelectionResult result;
        try
        {
            var candidates = _integration.SelectCandidates(_processes.GetProcesses(_integration.ProcessName));
            result = ProcessSelector.Select(candidates, bridge: null, verifiedOwnership: null);
        }
        catch (Exception ex)
        {
            _log?.Invoke("live tracking setup: process selection failed: " + ex.GetType().Name);
            return null;
        }

        if (result.Selection != ProcessSelectionStatus.Unique || result.SelectedProcess is not { } selected)
            return null;
        return new ProcessFingerprint(selected.ProcessId, selected.ProcessStartUtc) == game.Process ? selected : null;
    }

    /// <summary>
    /// The persistent environment takes priority over longshot_env.txt. Conservative:
    /// a differing value (including empty), or User and Machine disagreeing, refuses.
    /// </summary>
    private LiveTrackingSetupResult? CheckPersistentEnvironment()
    {
        string? user, machine;
        try
        {
            user = _environment.Read(LongshotEnvFile.SettingName, EnvironmentVariableTarget.User);
            machine = _environment.Read(LongshotEnvFile.SettingName, EnvironmentVariableTarget.Machine);
        }
        catch (Exception ex)
        {
            _log?.Invoke("live tracking setup: the persistent environment could not be read: " + ex.GetType().Name);
            return LiveTrackingSetupResult.EnvironmentUnreadable;
        }

        if (user is null && machine is null)
            return null;
        if (user is not null && machine is not null && !string.Equals(user, machine, StringComparison.Ordinal))
            return LiveTrackingSetupResult.EnvironmentConflict;

        return string.Equals(user ?? machine, LongshotEnvFile.SettingValue, StringComparison.Ordinal)
            ? LiveTrackingSetupResult.AlreadyEnabledByEnvironment
            : LiveTrackingSetupResult.EnvironmentConflict;
    }
}
