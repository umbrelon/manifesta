using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;

namespace Manifesta.Core.Tenant;

/// <summary>
/// Validates <see cref="TenantConfig"/> for structural consistency.
/// Pure (no I/O). Cross-references section definitions to check module flags.
/// </summary>
public sealed class TenantConfigValidator
{
    private readonly TenantConfig           _config;
    private readonly HashSet<string>        _moduleSectionNames;

    public TenantConfigValidator(TenantConfig config, IReadOnlyList<SectionDefinition> sections)
    {
        _config = config;
        _moduleSectionNames = sections
            .Where(s => s.IsModule == true)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public ValidationResult Validate()
    {
        var issues = new List<ValidationIssue>();

        // ── Rule 1: at least one root type ────────────────────────────────────
        var rootTypeNames = _config.Types
            .Where(kv => kv.Value.Root)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (rootTypeNames.Count == 0)
            issues.Add(Error("TENANT-ROOT-MISSING",
                "No tenant type has 'root: true'; exactly one database must be the root"));

        // ── Per-database checks ───────────────────────────────────────────────
        var rootDatabaseNames = new List<string>();

        foreach (var (dbName, db) in _config.Databases)
        {
            bool typeKnown = _config.Types.ContainsKey(db.Type);

            // Rule 3: type must exist
            if (!typeKnown)
            {
                issues.Add(Error("TENANT-TYPE-UNKNOWN",
                    $"Database '{dbName}' references unknown type '{db.Type}'"));
            }

            if (typeKnown && rootTypeNames.Contains(db.Type))
                rootDatabaseNames.Add(dbName);

            // Rule 4: every installed section must be flagged isModule: true
            foreach (var section in db.Sections)
            {
                if (!_moduleSectionNames.Contains(section))
                    issues.Add(Error("TENANT-SECTION-NOT-MODULE",
                        $"Database '{dbName}' references section '{section}' which is not flagged as isModule: true"));
            }

            // Rule 5: all required sections for this type must be installed
            if (typeKnown && _config.Types.TryGetValue(db.Type, out var typeDef))
            {
                var installed = new HashSet<string>(db.Sections, StringComparer.OrdinalIgnoreCase);
                foreach (var required in typeDef.RequiredSections)
                {
                    if (!installed.Contains(required))
                        issues.Add(Error("TENANT-REQUIRED-SECTION-MISSING",
                            $"Database '{dbName}' (type '{db.Type}') is missing required section '{required}'"));
                }
            }

            if (db.Parent is not null)
            {
                // Rule 6: parent must exist
                if (!_config.Databases.ContainsKey(db.Parent))
                {
                    issues.Add(Error("TENANT-PARENT-UNKNOWN",
                        $"Database '{dbName}' references unknown parent '{db.Parent}'"));
                }
                else if (typeKnown && _config.Databases.TryGetValue(db.Parent, out var parentDb))
                {
                    // Rule 7: parent type must be in allowedParents
                    if (_config.Types.TryGetValue(db.Type, out typeDef) &&
                        !typeDef.AllowedParents.Contains(parentDb.Type, StringComparer.OrdinalIgnoreCase))
                    {
                        issues.Add(Error("TENANT-PARENT-NOT-ALLOWED",
                            $"Database '{dbName}' (type '{db.Type}') has parent '{db.Parent}' " +
                            $"(type '{parentDb.Type}') which is not in allowedParents: " +
                            $"[{string.Join(", ", typeDef.AllowedParents)}]"));
                    }
                }

                // Rule 10: root databases must not have a parent
                if (typeKnown && rootTypeNames.Contains(db.Type))
                    issues.Add(Error("TENANT-ROOT-HAS-PARENT",
                        $"Database '{dbName}' has root type '{db.Type}' but also specifies parent '{db.Parent}'; root databases must not have a parent"));
            }
            else
            {
                // Rule 9: non-root databases without a parent are orphans (warning)
                if (typeKnown && !rootTypeNames.Contains(db.Type))
                    issues.Add(Warning("TENANT-ORPHAN",
                        $"Database '{dbName}' (type '{db.Type}') has no parent; non-root databases should specify a parent"));
            }
        }

        // Rule 2: exactly one database may be of a root type
        if (rootDatabaseNames.Count > 1)
            issues.Add(Error("TENANT-ROOT-DUPLICATE",
                $"Multiple databases have a root type: {string.Join(", ", rootDatabaseNames.Select(n => $"'{n}'"))}; " +
                "exactly one database must be the root"));

        // Rule 8: parent references must not form a cycle
        DetectCycles(issues);

        return ValidationResult.FromIssues(issues);
    }

    // ── Cycle detection (DFS, white/gray/black) ───────────────────────────────

    private void DetectCycles(List<ValidationIssue> issues)
    {
        var white = new HashSet<string>(_config.Databases.Keys, StringComparer.OrdinalIgnoreCase);
        var gray  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var black = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dbName in _config.Databases.Keys)
        {
            if (white.Contains(dbName))
                VisitForCycle(dbName, white, gray, black, issues);
        }
    }

    private void VisitForCycle(
        string name,
        HashSet<string> white, HashSet<string> gray, HashSet<string> black,
        List<ValidationIssue> issues)
    {
        white.Remove(name);
        gray.Add(name);

        if (_config.Databases.TryGetValue(name, out var db) && db.Parent is not null)
        {
            var parent = db.Parent;

            if (gray.Contains(parent))
            {
                issues.Add(Error("TENANT-CYCLE",
                    $"Circular parent reference detected: database '{name}' → parent '{parent}' creates a cycle"));
            }
            else if (white.Contains(parent))
            {
                VisitForCycle(parent, white, gray, black, issues);
            }
        }

        gray.Remove(name);
        black.Add(name);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ValidationIssue Error(string code, string message) => new()
    {
        Severity = ValidationSeverity.Error,
        Code     = code,
        Message  = message,
    };

    private static ValidationIssue Warning(string code, string message) => new()
    {
        Severity = ValidationSeverity.Warning,
        Code     = code,
        Message  = message,
    };
}
