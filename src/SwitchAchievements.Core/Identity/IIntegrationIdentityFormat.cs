namespace SwitchAchievements.Core;

/// <summary>
/// An integration's identity-fact format: which fact keys it reports and which
/// values are canonical for each key. Implemented by each integration project
/// (for example the Longshot integration); used by the application's catalog
/// loader to reject non-canonical catalog values before a <see cref="GameCatalog"/>
/// is built.
///
/// Core only defines this generic contract. It never interprets an integration's
/// formats; the resolver compares already-canonical strings ordinally.
/// </summary>
public interface IIntegrationIdentityFormat
{
    /// <summary>The integration these rules belong to (see <see cref="IntegrationIdentifier"/>).</summary>
    string IntegrationId { get; }

    /// <summary>The exact set of identity fact keys a catalog matcher for this integration must use.</summary>
    IReadOnlyCollection<string> IdentityKeys { get; }

    /// <summary>The exact set of build fact keys every supported build for this integration must use.</summary>
    IReadOnlyCollection<string> BuildKeys { get; }

    /// <summary>
    /// True only when <paramref name="value"/> is in the canonical form for
    /// <paramref name="factKey"/>. Returns false for unknown keys. Never repairs or
    /// normalises the value.
    /// </summary>
    bool IsCanonical(string factKey, string value);
}
