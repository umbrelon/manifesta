using System.Text.Json;
using System.Text.Json.Serialization;
using Manifesta.Core;

namespace Manifesta.Core.Pipeline;

/// <summary>
/// Reusable base for all loaders that read a directory of <c>*.json</c> files
/// into a sorted, stable list of IR objects.
/// </summary>
/// <remarks>
/// Subclasses only need to implement <see cref="SetSourceFile"/> — everything
/// else (enumerate, sort, deserialise, error handling) is handled here.
/// </remarks>
public abstract class JsonFileLoader<T> : ILoader<T> where T : class
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Return a copy of <paramref name="item"/> with its <c>SourceFile</c>
    /// property set to <paramref name="filePath"/>.
    /// Typically implemented as <c>item with { SourceFile = filePath }</c>.
    /// </summary>
    protected abstract T SetSourceFile(T item, string filePath);

    /// <inheritdoc/>
    public Task<IReadOnlyList<T>> LoadAsync(
        string rootPath,
        CancellationToken ct = default)
        => LoadAsync(rootPath, skipDirs: [], ct);

    /// <summary>
    /// Like <see cref="LoadAsync(string, CancellationToken)"/> but skips any
    /// <c>*.json</c> file that lives under a directory whose name matches an
    /// entry in <paramref name="skipDirs"/> (case-insensitive, any depth).
    /// </summary>
    /// <remarks>
    /// Use this overload when loading from a project root that may contain
    /// infrastructure directories (e.g. <c>_</c>) holding config files that are
    /// not table definitions.
    /// </remarks>
    public async Task<IReadOnlyList<T>> LoadAsync(
        string                rootPath,
        IReadOnlyList<string> skipDirs,
        CancellationToken     ct = default)
    {
        if (!Directory.Exists(rootPath))
            return Array.Empty<T>();

        var skipSet = skipDirs.Count > 0
            ? new HashSet<string>(skipDirs, TableNames.Comparer)
            : null;

        var items = new List<T>();

        foreach (var file in Directory
            .EnumerateFiles(rootPath, "*.json", SearchOption.AllDirectories)
            .Where(f => skipSet is null || !IsUnderSkippedDir(f, rootPath, skipSet))
            .OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var item = JsonSerializer.Deserialize<T>(json, _options);

                if (item is not null)
                    items.Add(SetSourceFile(item, file));
            }
            catch (JsonException ex)
            {
                throw new ManifestaSchemException($"Invalid JSON in {file}: {ex.Message}", ex);
            }
        }

        return items.AsReadOnly();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when <paramref name="filePath"/> lives inside a
    /// directory component whose name is in <paramref name="skipDirs"/>.
    /// </summary>
    /// <example>
    /// <c>IsUnderSkippedDir("/root/_/config.json", "/root", {"_"})</c> → <c>true</c>
    /// </example>
    public static bool IsUnderSkippedDir(
        string          filePath,
        string          rootPath,
        HashSet<string> skipDirs)
    {
        var relative = Path.GetRelativePath(rootPath, filePath);
        var parts    = relative.Split(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Walk every component except the final filename.
        for (var i = 0; i < parts.Length - 1; i++)
            if (skipDirs.Contains(parts[i]))
                return true;

        return false;
    }
}
