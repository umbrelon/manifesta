using Manifesta.Core.Pipeline;

namespace Manifesta.Core;

/// <summary>
/// Global exit codes — matches the spec exactly.
/// </summary>
public enum ExitCode
{
    /// <summary>All operations completed without errors.</summary>
    Success = 0,

    /// <summary>Validation errors (invalid FK, missing table, invalid x-db-table…).</summary>
    ValidationErrors = 1,

    /// <summary>Fatal schema errors (invalid JSON/YAML, circular dependencies…).</summary>
    FatalSchemaErrors = 2,

    /// <summary>Release repo failure (git error, no write access, network failure…).</summary>
    ReleaseRepoFailure = 3,

    /// <summary>Config / invocation error (missing or invalid config, unknown --type, conflicting flags…).</summary>
    ConfigOrInvocationError = 4,

    /// <summary>Internal error — unexpected crash (bug).</summary>
    InternalError = 5,
}

/// <summary>
/// Resolves the process exit code from a <see cref="ValidationResult"/> and the active flags.
/// </summary>
public static class ExitCodeResolver
{
    /// <summary>
    /// Compute the exit code for a completed validation pass.
    /// </summary>
    /// <param name="result">The aggregated validation result.</param>
    /// <param name="strict">
    ///   When <c>true</c>, warnings are promoted to errors (--strict flag).
    ///   Corresponds to <see cref="ExitCode.ValidationErrors"/> for any warning.
    /// </param>
    /// <param name="warnOnly">
    ///   When <c>true</c>, exit 0 even if warnings exist.
    ///   Errors still produce <see cref="ExitCode.ValidationErrors"/>.
    ///   Takes effect only when <paramref name="strict"/> is <c>false</c>.
    /// </param>
    public static ExitCode FromValidationResult(
        ValidationResult result,
        bool strict   = false,
        bool warnOnly = false)
    {
        if (result.HasErrors)
            return ExitCode.ValidationErrors;

        if (result.HasWarnings && strict)
            return ExitCode.ValidationErrors;

        return ExitCode.Success;
    }

    /// <summary>
    /// Wrap a fatal exception into the appropriate exit code.
    /// </summary>
    public static ExitCode FromException(Exception ex) => ex switch
    {
        ManifestaSchemException   => ExitCode.FatalSchemaErrors,
        ManifestaConfigException  => ExitCode.ConfigOrInvocationError,
        ManifestaReleaseException => ExitCode.ReleaseRepoFailure,
        _                        => ExitCode.InternalError,
    };
}

// ─── Typed exceptions ─────────────────────────────────────────────────────

/// <summary>Thrown when input JSON/YAML is malformed or fails schema validation fatally.</summary>
public sealed class ManifestaSchemException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Thrown for invalid configuration or flag combinations.</summary>
public sealed class ManifestaConfigException(string message)
    : Exception(message);

/// <summary>Thrown when a git/release operation fails.</summary>
public sealed class ManifestaReleaseException(string message, Exception? inner = null)
    : Exception(message, inner);
