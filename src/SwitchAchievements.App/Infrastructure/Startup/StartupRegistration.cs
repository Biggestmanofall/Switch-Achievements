using System.IO;
using Microsoft.Win32;
using SwitchAchievements.App.Infrastructure.Logging;

namespace SwitchAchievements.App.Infrastructure.Startup;

/// <summary>Registers (or unregisters) the application to start when the user signs in to Windows.</summary>
public interface IStartupRegistration
{
    /// <summary>True when the application's startup entry exists.</summary>
    bool IsRegistered();

    /// <summary>
    /// Makes the registration match <paramref name="enabled"/>. Idempotent. Registry
    /// failures (for example <see cref="UnauthorizedAccessException"/>) propagate to the caller.
    /// </summary>
    void Apply(bool enabled);
}

/// <summary>
/// Current-user startup registration (M5.7 Task 4) through the per-user Run key,
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. Needs no administrator
/// rights. Only the single value named <see cref="ValueName"/> is ever written or
/// deleted; no other value or key (including Explorer's <c>StartupApproved</c>) is touched.
///
/// <para>Limitations: this is the mechanism for an unpackaged desktop application (an
/// MSIX package would need a <c>StartupTask</c> instead). If the user disables the entry
/// in Task Manager, Windows records that in <c>StartupApproved</c>, which this class
/// deliberately leaves alone, so Windows will not launch the app even though the entry exists.</para>
/// </summary>
public sealed class StartupRegistration : IStartupRegistration
{
    /// <summary>The per-user Run key, relative to <see cref="Registry.CurrentUser"/>.</summary>
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The only value this class writes or deletes.</summary>
    public const string ValueName = "SwitchAchievements";

    private readonly IAppLogger _logger;
    private readonly RegistryKey _root;
    private readonly string _runKeyPath;
    private readonly string? _executablePath;

    /// <param name="logger">Application logger.</param>
    /// <param name="root">Registry root; defaults to <see cref="Registry.CurrentUser"/> (tests pass an isolated key).</param>
    /// <param name="runKeyPath">Run key path under <paramref name="root"/>; defaults to <see cref="DefaultRunKeyPath"/>.</param>
    /// <param name="executablePath">The executable to register; defaults to <see cref="Environment.ProcessPath"/>.</param>
    public StartupRegistration(IAppLogger logger, RegistryKey? root = null, string? runKeyPath = null, string? executablePath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _root = root ?? Registry.CurrentUser;
        _runKeyPath = runKeyPath ?? DefaultRunKeyPath;
        _executablePath = executablePath ?? Environment.ProcessPath;
    }

    /// <summary>
    /// The quoted command line to register, or null when the current process is not the
    /// application's own executable (for example <c>dotnet.exe</c> running the dll).
    /// </summary>
    public static string? CommandFor(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
            return null;
        if (!string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(Path.GetFileName(executablePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        return "\"" + executablePath + "\"";
    }

    public bool IsRegistered()
    {
        using var key = _root.OpenSubKey(_runKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    public void Apply(bool enabled)
    {
        if (enabled)
            Register();
        else
            Unregister();
    }

    private void Register()
    {
        var command = CommandFor(_executablePath);
        if (command is null)
        {
            _logger.Warn("start with Windows was not registered: the application is not running from its own executable");
            return;
        }

        using var key = _root.CreateSubKey(_runKeyPath, writable: true);
        if (key.GetValue(ValueName) is string existing && string.Equals(existing, command, StringComparison.Ordinal))
            return; // already registered for this executable

        key.SetValue(ValueName, command, RegistryValueKind.String);
        _logger.Info("start with Windows registered");
    }

    private void Unregister()
    {
        using var key = _root.OpenSubKey(_runKeyPath, writable: true);
        if (key?.GetValue(ValueName) is null)
            return;

        key.DeleteValue(ValueName, throwOnMissingValue: false);
        _logger.Info("start with Windows unregistered");
    }
}
