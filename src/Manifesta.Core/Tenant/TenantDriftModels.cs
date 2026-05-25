using Manifesta.Core.Drift;

namespace Manifesta.Core.Tenant;

/// <summary>
/// Full result of a <c>db tenant-drift</c> run — one entry per database in the topology.
/// </summary>
public sealed record TenantDriftSession
{
    public IReadOnlyList<TenantDatabaseDriftResult> Results     { get; init; } = [];
    public bool                                     HasDrift    { get; init; }
    public bool                                     HasWarnings { get; init; }
}

/// <summary>
/// Drift result for a single database in the tenant topology.
/// Wraps the existing <see cref="DriftSession"/> (scoped to that database's installed modules)
/// and adds topology metadata.
/// </summary>
public sealed record TenantDatabaseDriftResult
{
    public required string               DatabaseName     { get; init; }
    public required string               DatabaseType     { get; init; }
    public string?                       Parent           { get; init; }
    public IReadOnlyList<string>         InstalledModules { get; init; } = [];

    /// <summary>Drift results scoped to the tables belonging to this database's installed modules.</summary>
    public required DriftSession         Drift            { get; init; }

    /// <summary>
    /// Number of databases that are descendants of this database in the tenant tree.
    /// A drift on a high-subtree-size node affects more downstream databases.
    /// </summary>
    public int                           SubtreeSize      { get; init; }
}
