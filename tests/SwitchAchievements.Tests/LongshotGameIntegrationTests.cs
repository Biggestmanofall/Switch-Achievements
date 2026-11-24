using SwitchAchievements.Core;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.Tests;

public class LongshotGameIntegrationTests
{
    [Theory]
    [InlineData(@"C:\Recomp Library\longshot\Longshot\bin\longshot.exe", true)]
    [InlineData(@"C:\Recomp Library\longshot\Longshot\longshot.exe", false)]
    [InlineData(@"C:\Recomp Library\longshot\Longshot\bin\other.exe", false)]
    [InlineData("", false)]
    public void CanHandle_identifies_only_the_game_binary(string path, bool expected)
    {
        var integration = new LongshotGameIntegration();
        var process = new GameProcessInfo(ProcessId: 1234, ExecutablePath: path, ProcessStartUtc: DateTime.UtcNow);

        Assert.Equal(expected, integration.CanHandle(process));
    }

    [Fact]
    public void Exposes_stable_identifiers()
    {
        var integration = new LongshotGameIntegration();

        Assert.Equal("longshot", integration.Id);
        Assert.Equal("longshot", integration.ProcessName);
        Assert.False(string.IsNullOrWhiteSpace(integration.DisplayName));
        Assert.Equal("longshot_control", integration.PipeName);
    }
}