using System.IO;
using System.IO.Pipes;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;
using SwitchAchievements.Tests.Fakes;

namespace SwitchAchievements.Tests.BridgeAvailability;

/// <summary>M5.8.1: detecting the control bridge's pipe without connecting to it.</summary>
public class NamedPipeDirectoryTests
{
    private static string UniquePipeName() => "sa_pipe_test_" + Guid.NewGuid().ToString("N");

    // ---------------- the real Windows pipe namespace ----------------

    [Fact]
    public void An_existing_pipe_is_detected_and_a_closed_one_is_not()
    {
        var name = UniquePipeName();
        var directory = new WindowsNamedPipeDirectory();

        using (new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            Assert.True(directory.Exists(name));
            Assert.True(directory.Exists(@"\\.\pipe\" + name)); // the prefix is optional
        }

        Assert.False(directory.Exists(name));
    }

    [Fact]
    public void Detection_does_not_connect_to_the_pipe()
    {
        var name = UniquePipeName();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connection = server.WaitForConnectionAsync();

        Assert.True(new WindowsNamedPipeDirectory().Exists(name));
        Assert.False(connection.IsCompleted); // no client ever connected
        Assert.False(server.IsConnected);
    }

    [Fact]
    public void A_nonexistent_pipe_is_absent()
        => Assert.False(new WindowsNamedPipeDirectory().Exists(UniquePipeName()));

    [Fact]
    public void Names_are_compared_ignoring_case()
    {
        var name = UniquePipeName();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Assert.True(new WindowsNamedPipeDirectory().Exists(name.ToUpperInvariant()));
    }

    // ---------------- the listing seam ----------------

    [Fact]
    public void Listed_names_match_with_or_without_the_prefix_and_ignoring_case()
    {
        var directory = new WindowsNamedPipeDirectory(() => new[] { @"\\.\pipe\other", @"\\.\pipe\LongShot_Control" });

        Assert.True(directory.Exists("longshot_control"));
        Assert.True(directory.Exists(@"\\.\PIPE\longshot_control"));
        Assert.False(directory.Exists("longshot"));
        Assert.False(directory.Exists("longshot_control_2"));
    }

    [Fact]
    public void Nested_pipe_names_are_compared_whole()
    {
        var directory = new WindowsNamedPipeDirectory(() => new[] { @"\\.\pipe\localhost\longshot_control" });

        Assert.False(directory.Exists("longshot_control"));
        Assert.True(directory.Exists(@"localhost\longshot_control"));
    }

    public static TheoryData<Exception> ListingFailures => new()
    {
        new IOException("listing failed"),
        new UnauthorizedAccessException("denied"),
        new InvalidOperationException("unexpected"),
    };

    [Theory]
    [MemberData(nameof(ListingFailures))]
    public void A_listing_failure_is_unknown_not_absent(Exception failure)
    {
        var directory = new WindowsNamedPipeDirectory(() => throw failure);
        Assert.Null(directory.Exists("longshot_control"));
    }

    [Fact]
    public void A_failure_part_way_through_the_listing_is_unknown()
    {
        static IEnumerable<string> Listing()
        {
            yield return @"\\.\pipe\other";
            throw new IOException("the listing broke");
        }

        Assert.Null(new WindowsNamedPipeDirectory(Listing).Exists("longshot_control"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_is_unknown(string name)
        => Assert.Null(new WindowsNamedPipeDirectory(() => Array.Empty<string>()).Exists(name));

    // ---------------- the Longshot integration's mapping ----------------

    private sealed class ScriptedPipeDirectory : INamedPipeDirectory
    {
        public bool? Result { get; set; }
        public List<string> Asked { get; } = new();

        public bool? Exists(string pipeName)
        {
            Asked.Add(pipeName);
            return Result;
        }
    }

    [Theory]
    [InlineData(true, EndpointAvailability.Available)]
    [InlineData(false, EndpointAvailability.Absent)]
    [InlineData(null, EndpointAvailability.Unknown)]
    public async Task The_integration_maps_pipe_existence_to_endpoint_availability(bool? exists, EndpointAvailability expected)
    {
        var directory = new ScriptedPipeDirectory { Result = exists };
        await using var integration = new LongshotGameIntegration("longshot_control", new FakeCommandLineReader(), directory);

        var availability = integration.CheckEndpoint(new GameProcessInfo(100, @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe", DateTime.UtcNow));

        Assert.Equal(expected, availability);
        Assert.Equal(new[] { "longshot_control" }, directory.Asked);
    }

    [Fact]
    public async Task The_integration_checks_its_own_pipe_name()
    {
        var directory = new ScriptedPipeDirectory { Result = true };
        await using var integration = new LongshotGameIntegration("custom_pipe", new FakeCommandLineReader(), directory);

        integration.CheckEndpoint(new GameProcessInfo(1, "x", DateTime.UtcNow));

        Assert.Equal(new[] { "custom_pipe" }, directory.Asked);
    }

    [Fact]
    public void The_interface_default_is_unknown()
    {
        IGameIntegration integration = new MinimalIntegration();
        Assert.Equal(EndpointAvailability.Unknown, integration.CheckEndpoint(new GameProcessInfo(1, "x", DateTime.UtcNow)));
    }

    /// <summary>An integration that does not override <see cref="IGameIntegration.CheckEndpoint"/>.</summary>
    private sealed class MinimalIntegration : IGameIntegration
    {
        public string Id => "minimal";
        public string DisplayName => "Minimal";
        public string ProcessName => "minimal";
        public string PipeName => "minimal";
        public bool CanHandle(GameProcessInfo process) => true;
        public Task ConnectAsync(GameProcessInfo process, CancellationToken ct) => Task.CompletedTask;
        public Task SubscribeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UnsubscribeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<NormalizedSnapshot?> GetSnapshotAsync(CancellationToken ct) => Task.FromResult<NormalizedSnapshot?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public event Action<string>? StatusChanged { add { } remove { } }
        public event Action<NormalizedSnapshot>? SnapshotReceived { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
    }
}
