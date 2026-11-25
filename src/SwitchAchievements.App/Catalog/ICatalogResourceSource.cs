using System.IO;

namespace SwitchAchievements.App.Catalog;

/// <summary>
/// Read-only access to the catalog's JSON documents by resource name (for
/// example <c>catalog/catalog.json</c>). Production uses the application's
/// embedded resources; tests use an in-memory implementation.
/// </summary>
public interface ICatalogResourceSource
{
    /// <summary>
    /// Every candidate catalog resource name — anything under <c>catalog</c>,
    /// including incorrectly named entries, so the loader can report them rather
    /// than silently ignore them.
    /// </summary>
    IReadOnlyCollection<string> Names { get; }

    /// <summary>Opens a resource for reading, or returns null when it does not exist.</summary>
    Stream? Open(string name);
}
