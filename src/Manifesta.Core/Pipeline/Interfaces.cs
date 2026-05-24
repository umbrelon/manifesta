namespace Manifesta.Core.Pipeline;

// ─── Validation types ─────────────────────────────────────────────────────

/// <summary>
/// A single diagnostic item emitted during validation.
/// </summary>
public sealed record ValidationIssue
{
    public required ValidationSeverity Severity { get; init; }
    public required string             Code     { get; init; }  // e.g. "MANIFESTA-1001"
    public required string             Message  { get; init; }
    public string? File    { get; init; }
    public string? Field   { get; init; }
    public int?    Line    { get; init; }
}

public enum ValidationSeverity { Warning, Error }

/// <summary>
/// The aggregated result of one or more validation passes.
/// </summary>
public sealed record ValidationResult
{
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    public bool HasErrors   => Issues.Any(i => i.Severity == ValidationSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == ValidationSeverity.Warning);

    public static ValidationResult Empty => new();

    public static ValidationResult FromIssues(IEnumerable<ValidationIssue> issues)
        => new() { Issues = issues.ToList() };

    public ValidationResult Merge(ValidationResult other)
        => new() { Issues = Issues.Concat(other.Issues).ToList() };
}

// ─── Phase interfaces ─────────────────────────────────────────────────────

/// <summary>
/// Loads input files from the filesystem into IR objects.
/// Implementations must be parallel-safe (each file is independent).
/// </summary>
public interface ILoader<T>
{
    /// <summary>
    /// Load all relevant files from <paramref name="rootPath"/>.
    /// Returns a sorted, stable list (by file path) for determinism.
    /// </summary>
    Task<IReadOnlyList<T>> LoadAsync(string rootPath, CancellationToken ct = default);
}

/// <summary>
/// Validates IR objects and returns structured diagnostics.
/// Implementations must be stateless (parallel-safe).
/// </summary>
public interface IValidator<T>
{
    /// <summary>Validate a single IR item.</summary>
    ValidationResult Validate(T item);

    /// <summary>Validate a collection, merging all results.</summary>
    ValidationResult ValidateAll(IEnumerable<T> items)
        => items.Select(Validate).Aggregate(ValidationResult.Empty, (acc, r) => acc.Merge(r));
}

/// <summary>
/// Transforms IR into output content. Parallel-safe: each output is independent.
/// </summary>
public interface IGenerator<TInput, TOutput>
{
    TOutput Generate(TInput input);
}

/// <summary>
/// Writes generated content to disk.
/// In dry-run mode, the implementation writes to stdout instead.
/// Writes are always sequential and in a defined order.
/// </summary>
public interface IWriter
{
    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="path"/>.
    /// Uses atomic write (temp file → rename).
    /// Is a no-op when <see cref="IsDryRun"/> is true.
    /// </summary>
    Task WriteAsync(string path, string content, CancellationToken ct = default);

    /// <summary>True when running in dry-run mode (no files will be written).</summary>
    bool IsDryRun { get; }
}
