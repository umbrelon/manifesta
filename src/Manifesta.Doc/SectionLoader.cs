using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;

namespace Manifesta.Doc;

/// <summary>
/// Loads section definitions from JSON files under a root directory.
/// Implements <see cref="ILoader{T}"/> for <see cref="SectionDefinition"/>.
///
/// Recursively scans for <c>*.json</c> files, deserializes them to
/// <see cref="SectionDefinition"/> objects, and returns results sorted by
/// file path for determinism.
/// </summary>
public sealed class SectionLoader : JsonFileLoader<SectionDefinition>
{
    /// <inheritdoc/>
    protected override SectionDefinition SetSourceFile(SectionDefinition item, string filePath)
        => item with { SourceFile = filePath };
}
