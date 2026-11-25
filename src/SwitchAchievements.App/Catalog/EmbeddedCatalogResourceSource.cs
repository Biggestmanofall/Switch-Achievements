using System.IO;
using System.Reflection;

namespace SwitchAchievements.App.Catalog;

/// <summary>
/// <see cref="ICatalogResourceSource"/> over the manifest resources embedded in
/// an assembly (by default this application's). No file-system access: the
/// developer-owned catalog travels inside the application.
/// Names are reported exactly as embedded — never normalised — so an incorrectly
/// embedded name (for example one using backslashes) is reported by the loader.
/// </summary>
public sealed class EmbeddedCatalogResourceSource : ICatalogResourceSource
{
    private readonly Assembly _assembly;

    public EmbeddedCatalogResourceSource()
        : this(typeof(EmbeddedCatalogResourceSource).Assembly)
    {
    }

    public EmbeddedCatalogResourceSource(Assembly assembly)
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        Names = Array.AsReadOnly(assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("catalog/", StringComparison.Ordinal) || n.StartsWith("catalog\\", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray());
    }

    public IReadOnlyCollection<string> Names { get; }

    public Stream? Open(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _assembly.GetManifestResourceStream(name);
    }
}
