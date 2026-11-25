using System.Text;
using SwitchAchievements.App.Catalog;

namespace SwitchAchievements.Tests.Catalog;

/// <summary>
/// In-memory <see cref="ICatalogResourceSource"/> for loader tests. Names are
/// enumerated in insertion order (so tests can vary it). A resource whose content
/// is null is listed but cannot be opened.
/// </summary>
internal sealed class InMemoryCatalogResourceSource : ICatalogResourceSource
{
    private readonly List<KeyValuePair<string, string?>> _resources;

    public InMemoryCatalogResourceSource(IEnumerable<KeyValuePair<string, string?>> resources)
    {
        _resources = resources.ToList();
        Names = _resources.Select(r => r.Key).ToArray();
    }

    public InMemoryCatalogResourceSource(params (string Name, string? Content)[] resources)
        : this(resources.Select(r => new KeyValuePair<string, string?>(r.Name, r.Content)))
    {
    }

    public IReadOnlyCollection<string> Names { get; }

    public int OpenCount { get; private set; }

    public Stream? Open(string name)
    {
        OpenCount++;
        foreach (var (key, content) in _resources)
        {
            if (string.Equals(key, name, StringComparison.Ordinal))
                return content is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        }

        return null;
    }

    /// <summary>The same resources, enumerated in reverse order.</summary>
    public InMemoryCatalogResourceSource Reversed()
        => new(Enumerable.Reverse(_resources));
}
