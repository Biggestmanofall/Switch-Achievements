using SwitchAchievements.App.Catalog;
using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;
using SwitchAchievements.Tests.Identity;

namespace SwitchAchievements.Tests.MonitoringIdentity;

/// <summary>M5.6.5: GameIdentifier = provider evidence + catalog resolution.</summary>
public class GameIdentifierTests
{
    private static readonly GameProcessInfo Selected =
        new(100, @"C:\Games\Longshot\bin\longshot.exe", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static GameCatalog Embedded()
        => CatalogLoader.LoadEmbedded(new IIntegrationIdentityFormat[] { new LongshotIdentityFormat() }).Catalog!;

    [Fact]
    public async Task Identified_evidence_resolves_to_ocarina_of_time_with_both_catalogs()
    {
        foreach (var catalog in new[] { IdentityTestData.OotCatalog(), Embedded() })
        {
            var identifier = new GameIdentifier(new FakeIdentityProvider(), catalog);
            var result = await identifier.IdentifyAsync(Selected, CancellationToken.None);

            Assert.True(identifier.IsAvailable);
            Assert.NotNull(result);
            Assert.Equal(IdentificationStatus.Identified, result.Status);
            Assert.Equal("zelda-ocarina-of-time", result.Game!.GameId.Value);
            Assert.Equal(new ProcessFingerprint(Selected.ProcessId, Selected.ProcessStartUtc), result.Game.Process);
        }
    }

    [Theory]
    [InlineData("unknown", IdentificationStatus.Unknown)]
    [InlineData("unsupported", IdentificationStatus.Unsupported)]
    [InlineData("conflict", IdentificationStatus.Conflict)]
    public async Task Non_identified_results_pass_through_unchanged(string kind, IdentificationStatus expected)
    {
        var provider = new FakeIdentityProvider { Respond = p => IdentityEvidence.For(kind, p) };
        var result = await new GameIdentifier(provider, Embedded()).IdentifyAsync(Selected, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Game);
        Assert.False(result.IsIdentified);
    }

    [Fact]
    public async Task Without_a_catalog_identification_is_unavailable_and_the_provider_is_not_called()
    {
        var provider = new FakeIdentityProvider();
        var identifier = new GameIdentifier(provider, catalog: null);

        Assert.False(identifier.IsAvailable);
        Assert.Null(await identifier.IdentifyAsync(Selected, CancellationToken.None));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task Unexpected_provider_exceptions_propagate()
    {
        var provider = new FakeIdentityProvider { Throw = new InvalidOperationException("bug") };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GameIdentifier(provider, Embedded()).IdentifyAsync(Selected, CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GameIdentifier(new FakeIdentityProvider(), Embedded()).IdentifyAsync(Selected, cts.Token));

        var gated = new FakeIdentityProvider { FirstCallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var cts2 = new CancellationTokenSource();
        var pending = new GameIdentifier(gated, Embedded()).IdentifyAsync(Selected, cts2.Token);
        await gated.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(gated.ObservedCancellation);
    }

    [Fact]
    public async Task Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => new GameIdentifier(null!, null));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new GameIdentifier(new FakeIdentityProvider(), null).IdentifyAsync(null!, CancellationToken.None));
    }
}
