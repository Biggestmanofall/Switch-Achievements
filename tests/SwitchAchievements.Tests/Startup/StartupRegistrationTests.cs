using Microsoft.Win32;
using SwitchAchievements.App.Infrastructure.Startup;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.Startup;

/// <summary>
/// M5.7 Task 4: current-user startup registration. Every test works in its own throwaway
/// key, HKCU\Software\SwitchAchievements.Tests\{guid}\Run, never the real Run key, and
/// deletes it afterwards.
/// </summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private const string TestRoot = @"Software\SwitchAchievements.Tests";
    private const string AppExe = @"C:\Program Files\Switch Achievements\SwitchAchievements.App.exe";

    private readonly string _testKey = TestRoot + @"\" + Guid.NewGuid().ToString("N");
    private readonly string _runKey;
    private readonly CapturingLogger _logger = new();

    public StartupRegistrationTests()
    {
        _runKey = _testKey + @"\Run";
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_testKey, throwOnMissingSubKey: false);
        using var root = Registry.CurrentUser.OpenSubKey(TestRoot, writable: false);
        if (root is not null && root.SubKeyCount == 0 && root.ValueCount == 0)
        {
            root.Dispose();
            Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
        }
    }

    private StartupRegistration Registration(string? exe = AppExe)
        => new(_logger, Registry.CurrentUser, _runKey, exe);

    private object? ReadValue(string name = StartupRegistration.ValueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey, writable: false);
        return key?.GetValue(name);
    }

    private void WriteValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    [Fact]
    public void The_production_registration_targets_the_current_user_run_key()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.DefaultRunKeyPath);
        Assert.Equal("SwitchAchievements", StartupRegistration.ValueName);
    }

    [Fact]
    public void Default_off_creates_no_registration()
    {
        var registration = Registration();
        registration.Apply(false);

        Assert.False(registration.IsRegistered());
        Assert.Null(ReadValue());
        using var key = Registry.CurrentUser.OpenSubKey(_runKey);
        Assert.Null(key); // not even the key is created
    }

    [Fact]
    public void Enabling_writes_the_quoted_executable_path()
    {
        var registration = Registration();
        registration.Apply(true);

        Assert.True(registration.IsRegistered());
        Assert.Equal("\"" + AppExe + "\"", ReadValue());
        using var key = Registry.CurrentUser.OpenSubKey(_runKey)!;
        Assert.Equal(RegistryValueKind.String, key.GetValueKind(StartupRegistration.ValueName));
    }

    [Fact]
    public void Enabling_again_is_idempotent()
    {
        var registration = Registration();
        registration.Apply(true);
        registration.Apply(true);

        Assert.Equal("\"" + AppExe + "\"", ReadValue());
        Assert.Equal(1, _logger.Count("start with Windows registered")); // the second call wrote nothing
        using var key = Registry.CurrentUser.OpenSubKey(_runKey)!;
        Assert.Single(key.GetValueNames());
    }

    [Fact]
    public void A_stale_path_is_rewritten()
    {
        WriteValue(StartupRegistration.ValueName, @"""C:\Old Location\SwitchAchievements.App.exe""");

        Registration().Apply(true);

        Assert.Equal("\"" + AppExe + "\"", ReadValue());
    }

    [Fact]
    public void Disabling_removes_only_our_value_and_leaves_others_untouched()
    {
        WriteValue("OtherApp", @"""C:\Other\other.exe"" --tray");
        WriteValue("SwitchAchievementsHelper", "unrelated");
        var registration = Registration();
        registration.Apply(true);

        registration.Apply(false);

        Assert.False(registration.IsRegistered());
        Assert.Null(ReadValue());
        Assert.Equal(@"""C:\Other\other.exe"" --tray", ReadValue("OtherApp"));
        Assert.Equal("unrelated", ReadValue("SwitchAchievementsHelper"));
    }

    [Fact]
    public void Enabling_leaves_other_values_untouched()
    {
        WriteValue("OtherApp", "other");
        Registration().Apply(true);

        Assert.Equal("other", ReadValue("OtherApp"));
    }

    [Fact]
    public void Disabling_when_absent_is_harmless()
    {
        WriteValue("OtherApp", "other");
        var registration = Registration();

        registration.Apply(false);
        registration.Apply(false);

        Assert.False(registration.IsRegistered());
        Assert.Equal("other", ReadValue("OtherApp"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"C:\Program Files\dotnet\DOTNET.EXE")]
    [InlineData(@"C:\app\SwitchAchievements.App.dll")]
    [InlineData("SwitchAchievements.App.exe")] // not fully qualified
    [InlineData("")]
    public void A_non_apphost_process_is_never_registered(string exe)
    {
        var registration = Registration(exe);
        registration.Apply(true);

        Assert.False(registration.IsRegistered());
        Assert.Null(ReadValue());
        Assert.Equal(1, _logger.Count("start with Windows was not registered"));
    }

    [Fact]
    public void Dotnet_host_does_not_remove_an_existing_registration_when_enabling()
    {
        WriteValue(StartupRegistration.ValueName, "\"" + AppExe + "\"");
        Registration(@"C:\Program Files\dotnet\dotnet.exe").Apply(true);

        Assert.Equal("\"" + AppExe + "\"", ReadValue());
    }

    [Fact]
    public void Command_quotes_paths_with_spaces()
        => Assert.Equal("\"" + AppExe + "\"", StartupRegistration.CommandFor(AppExe));
}
