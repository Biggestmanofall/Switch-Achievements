using SwitchAchievements.Longshot;

namespace SwitchAchievements.Tests.LiveTrackingSetup;

/// <summary>M5.8.2: resolving a Longshot game folder from the game executable path.</summary>
public class LongshotGameFolderTests
{
    private const string Exe = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private const string Folder = @"C:\Recomp Library\longshot\Longshot";

    private static readonly Func<string, bool> Local = _ => false;
    private static readonly Func<string, bool> ImagePresent = path => path == Folder + @"\image.bin";

    [Fact]
    public void A_valid_layout_resolves_the_folder_above_bin()
    {
        Assert.True(LongshotGameFolder.TryResolveInstallation(Exe, Local, ImagePresent, out var folder, out var failure));
        Assert.Equal(Folder, folder);
        Assert.Equal(string.Empty, failure);
        Assert.Equal(Folder + @"\" + LongshotEnvFile.FileName, Path.Join(folder, LongshotEnvFile.FileName));
    }

    [Fact]
    public void A_missing_image_bin_is_rejected()
    {
        Assert.False(LongshotGameFolder.TryResolveInstallation(Exe, Local, _ => false, out var folder, out var failure));
        Assert.Equal(string.Empty, folder);
        Assert.Equal("the game folder has no image.bin", failure);
    }

    [Fact]
    public void Only_image_bin_in_the_resolved_folder_counts()
    {
        var asked = new List<string>();
        LongshotGameFolder.TryResolveInstallation(Exe, Local, p => { asked.Add(p); return false; }, out _, out _);
        Assert.Equal(new[] { Folder + @"\image.bin" }, asked);
    }

    public static TheoryData<string?, string> Rejected => new()
    {
        { null, "the selected executable path is empty" },
        { "", "the selected executable path is empty" },
        { @"\\?\C:\Recomp Library\longshot\Longshot\bin\longshot.exe", "device-namespace executable paths are not supported" },
        { @"\\.\C:\Recomp Library\longshot\Longshot\bin\longshot.exe", "device-namespace executable paths are not supported" },
        { @"\\server\share\Longshot\bin\longshot.exe", "network (UNC) executable paths are not supported" },
        { @"Longshot\bin\longshot.exe", "the selected executable path is not fully qualified" },
        { @"C:\Recomp Library\longshot\Longshot\bin\..\bin\longshot.exe", "the selected executable path is not normalized" },
        { @"C:\Recomp Library\longshot\Longshot\longshot.exe", @"the selected executable is not a Longshot game binary (expected ...\bin\longshot.exe)" },
        { @"C:\Recomp Library\longshot\Longshot\bin\other.exe", @"the selected executable is not a Longshot game binary (expected ...\bin\longshot.exe)" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Invalid_paths_are_rejected_with_fixed_reasons(string? executable, string expectedFailure)
    {
        Assert.False(LongshotGameFolder.TryResolveInstallation(executable, Local, _ => true, out var folder, out var failure));
        Assert.Equal(string.Empty, folder);
        Assert.Equal(expectedFailure, failure);
        Assert.DoesNotContain(@"C:\", failure, StringComparison.Ordinal); // never a path
    }

    [Fact]
    public void A_network_drive_installation_is_rejected()
    {
        Assert.False(LongshotGameFolder.TryResolveInstallation(Exe, _ => true, _ => true, out _, out var failure));
        Assert.Equal("network-drive installations are not supported", failure);
    }

    [Fact]
    public void The_identity_provider_still_derives_its_exefs_folder_the_same_way()
    {
        var provider = new LongshotGameIdentityProvider();

        Assert.True(provider.TryGetExefsDirectory(Exe, out var exefs, out var failure));
        Assert.Equal(Folder + @"\.longshot_exefs", exefs);
        Assert.Equal(string.Empty, failure);

        Assert.False(provider.TryGetExefsDirectory(@"Longshot\bin\longshot.exe", out exefs, out failure));
        Assert.Equal(string.Empty, exefs);
        Assert.Equal("the selected executable path is not fully qualified", failure);
    }
}
