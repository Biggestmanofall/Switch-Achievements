namespace SwitchAchievements.Core;

/// <summary>Outcome of resolving identity evidence against the supported-game catalog.</summary>
public enum IdentificationStatus
{
    /// <summary>The evidence matched exactly one supported catalog game and one of its supported builds.</summary>
    Identified,

    /// <summary>The identity is known, but no supported catalog entry (game or build) matches.</summary>
    Unsupported,

    /// <summary>The identity could not be reliably established (missing or invalid evidence).</summary>
    Unknown,

    /// <summary>The available evidence disagrees with itself or with the catalog.</summary>
    Conflict,
}

/// <summary>
/// The authorization object for a successfully identified, supported game
/// running in one specific process instance.
///
/// <para><b>GameId is a label; IdentifiedGame is authorization.</b> Anyone can
/// construct a <see cref="Core.GameId"/>, and snapshots and sessions carry one
/// purely as data. Only an <see cref="IdentifiedGame"/> proves that a process's
/// evidence was resolved against the supported catalog (exact identity facts and
/// an explicitly supported build). Achievement evaluation must require an
/// <see cref="IdentifiedGame"/>, never a <see cref="Core.GameId"/> or a connected
/// session.</para>
///
/// <para>Construction boundary: the constructor is internal and only
/// <see cref="GameIdentityResolver"/> calls it. This is an assembly-level trust
/// boundary: code outside SwitchAchievements.Core cannot construct or modify an
/// instance; inside Core, the resolver is the only caller by convention.</para>
/// </summary>
public sealed class IdentifiedGame
{
    internal IdentifiedGame(
        GameId gameId,
        string displayName,
        string platform,
        string integrationId,
        CatalogBuild build,
        ProcessFingerprint process)
    {
        GameId = gameId;
        DisplayName = displayName;
        Platform = platform;
        IntegrationId = integrationId;
        Build = build;
        Process = process;
    }

    /// <summary>The stable catalog id, e.g. <c>zelda-ocarina-of-time</c>.</summary>
    public GameId GameId { get; }

    public string DisplayName { get; }

    public string Platform { get; }

    /// <summary>The integration whose evidence was matched.</summary>
    public string IntegrationId { get; }

    /// <summary>The supported build the evidence matched exactly.</summary>
    public CatalogBuild Build { get; }

    /// <summary>The process instance this identification is bound to.</summary>
    public ProcessFingerprint Process { get; }
}
