using SwitchAchievements.Core;

namespace SwitchAchievements.App.Services;

/// <summary>
/// Turns a selected game process into a <see cref="GameIdentification"/>: collects
/// integration-specific evidence through an <see cref="IGameIdentityProvider"/> and
/// resolves it against the supported-game catalog with the pure Core
/// <see cref="GameIdentityResolver"/> (M5.6.5).
///
/// <para>When no valid catalog is available (it failed to load), identification is
/// <i>unavailable</i>: <see cref="IdentifyAsync"/> returns null without calling the
/// provider. This is deliberately not an "Unsupported" result, which an empty
/// catalog would wrongly produce.</para>
///
/// <para>Expected provider failures are already reported as evidence issues by the
/// provider contract. Caller cancellation and unexpected (programming) exceptions
/// are not caught here; they propagate to the caller.</para>
/// </summary>
public sealed class GameIdentifier
{
    private readonly IGameIdentityProvider _provider;
    private readonly GameCatalog? _catalog;

    public GameIdentifier(IGameIdentityProvider provider, GameCatalog? catalog)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _catalog = catalog;
    }

    /// <summary>True when a validated catalog is available, so identification can run.</summary>
    public bool IsAvailable => _catalog is not null;

    /// <summary>
    /// Identifies the selected process, or returns null when identification is
    /// unavailable (no catalog).
    /// </summary>
    public async Task<GameIdentification?> IdentifyAsync(GameProcessInfo selected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ct.ThrowIfCancellationRequested();

        if (_catalog is null)
            return null;

        var evidence = await _provider.CollectAsync(selected, ct).ConfigureAwait(false);
        return GameIdentityResolver.Resolve(_catalog, evidence);
    }
}
