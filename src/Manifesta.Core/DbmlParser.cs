using System.Text;
using Manifesta.Core.IR;

namespace Manifesta.Core;

/// <summary>
/// Hand-rolled parser for the DBML schema format.
/// Covers the common subset required by <c>manifesta import dbml</c>:
/// Table blocks, column definitions (pk, not null, note), Ref entries, and TableGroup blocks.
///
/// Round-trip note: inline column notes of the form
/// <c>// calculated: ([expr]) PERSISTED</c> are parsed back into
/// <see cref="FieldDefinition.IsComputed"/>, <see cref="FieldDefinition.ComputedExpression"/>,
/// and <see cref="FieldDefinition.IsPersisted"/>.
/// </summary>
public sealed class DbmlParser
{
    public sealed record ParseResult(
        IReadOnlyList<TableDefinition>  Tables,
        IReadOnlyList<SectionDefinition> Sections,
        IReadOnlyList<string>            Errors);

    /// <param name="dbml">Full DBML source text.</param>
    /// <param name="schemaPrefix">
    /// When set, unqualified table names (no dot) are prefixed with this schema.
    /// e.g. <c>"dbo"</c> → <c>dbo.Customer</c>.
    /// </param>
    public ParseResult Parse(string dbml, string? schemaPrefix = null)
    {
        var lines        = dbml.ReplaceLineEndings("\n").Split('\n');
        var tables       = new List<TableDefinition>();
        var pendingRefs  = new List<PendingRef>();
        var pendingGroups = new List<PendingGroup>();
        var errors       = new List<string>();

        int i = 0;
        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//"))
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("Table ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Table\t", StringComparison.OrdinalIgnoreCase))
            {
                var (table, endLine, tableErrors) = ParseTable(lines, i, schemaPrefix);
                errors.AddRange(tableErrors);
                if (table is not null) tables.Add(table);
                i = endLine + 1;
                continue;
            }

            if (trimmed.StartsWith("Ref:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Ref ", StringComparison.OrdinalIgnoreCase))
            {
                var pending = ParseRef(trimmed, schemaPrefix);
                if (pending is not null)
                    pendingRefs.Add(pending);
                else
                    errors.Add($"Could not parse Ref: {trimmed}");
                i++;
                continue;
            }

            if (trimmed.StartsWith("TableGroup ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("TableGroup\t", StringComparison.OrdinalIgnoreCase))
            {
                var (group, endLine) = ParseTableGroup(lines, i);
                pendingGroups.Add(group);
                i = endLine + 1;
                continue;
            }

            i++;
        }

        // ── Apply refs to tables ─────────────────────────────────────────────

        var tableMap = tables.ToDictionary(t => t.Name, TableNames.Comparer);

        foreach (var r in pendingRefs)
        {
            if (!tableMap.TryGetValue(r.SourceTable, out var src))
            {
                errors.Add($"Ref refers to unknown table '{r.SourceTable}'");
                continue;
            }

            var fk = new ForeignKey
            {
                SourceField = r.SourceField,
                TargetTable = r.TargetTable,
                TargetField = r.TargetField,
                Kind        = r.Kind,
            };

            tableMap[r.SourceTable] = src with
            {
                ForeignKeys = src.ForeignKeys.Append(fk).ToList(),
            };
        }

        var finalTables   = tables.Select(t => tableMap.GetValueOrDefault(t.Name, t)).ToList();
        var finalSections = pendingGroups.Select(g => new SectionDefinition
        {
            Name   = g.Name,
            Tables = g.Tables,
        }).ToList();

        return new ParseResult(finalTables, finalSections, errors);
    }

    // ── Table block ───────────────────────────────────────────────────────────

    private static (TableDefinition? Table, int EndLine, List<string> Errors) ParseTable(
        string[] lines, int startLine, string? schemaPrefix)
    {
        var errors     = new List<string>();
        var headerLine = lines[startLine].Trim();

        var tableName = ExtractBlockName(headerLine, "Table ");
        if (tableName is null)
        {
            errors.Add($"Could not parse table name: {headerLine}");
            return (null, startLine, errors);
        }

        if (!string.IsNullOrEmpty(schemaPrefix) && !tableName.Contains('.'))
            tableName = $"{schemaPrefix}.{tableName}";

        var fields    = new List<FieldDefinition>();
        string? tableNote = null;
        int depth   = 0;
        int endLine = startLine;

        for (int i = startLine; i < lines.Length; i++)
        {
            var raw     = lines[i];
            var trimmed = raw.Trim();

            if (trimmed.Contains('{')) depth++;
            if (trimmed.Contains('}'))
            {
                depth--;
                if (depth == 0) { endLine = i; break; }
            }

            if (i == startLine || depth != 1) continue;

            // Strip trailing line comment (outside brackets / strings)
            trimmed = StripLineComment(trimmed);
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("Note:", StringComparison.OrdinalIgnoreCase))
            {
                tableNote = ExtractNoteValue(trimmed);
            }
            else if (!trimmed.StartsWith("[") &&
                     !trimmed.StartsWith("indexes", StringComparison.OrdinalIgnoreCase))
            {
                var field = ParseField(trimmed, errors);
                if (field is not null) fields.Add(field);
            }
        }

        var primaryKey = fields
            .Where(f => f.IsPrimaryKey)
            .Select(f => f.Name)
            .ToList();

        var table = new TableDefinition
        {
            Name        = tableName,
            Description = tableNote ?? "",
            Fields      = fields,
            PrimaryKey  = primaryKey,
        };

        return (table, endLine, errors);
    }

    // ── Column definition ─────────────────────────────────────────────────────

    private static FieldDefinition? ParseField(string line, List<string> errors)
    {
        line = line.Trim();
        if (string.IsNullOrEmpty(line)) return null;

        // Extract column name (possibly quoted)
        string name;
        string rest;

        if (line.StartsWith('"'))
        {
            var closeQ = FindClosingQuote(line, 0);
            if (closeQ < 0) { errors.Add($"Unclosed quote in field: {line}"); return null; }
            name = line[1..closeQ];
            rest = line[(closeQ + 1)..].TrimStart();
        }
        else
        {
            var spaceIdx = line.IndexOfAny([' ', '\t']);
            if (spaceIdx < 0) return null;
            name = line[..spaceIdx];
            rest = line[(spaceIdx + 1)..].TrimStart();
        }

        // Extract type and optional constraints [...]
        string type;
        string? constraintStr = null;

        var bracketIdx = rest.IndexOf('[');
        if (bracketIdx >= 0)
        {
            type = rest[..bracketIdx].TrimEnd();
            var closeIdx = FindClosingBracket(rest, bracketIdx);
            constraintStr = closeIdx > bracketIdx
                ? rest[(bracketIdx + 1)..closeIdx]
                : rest[(bracketIdx + 1)..];
        }
        else
        {
            type = rest.TrimEnd();
        }

        if (string.IsNullOrEmpty(type)) return null;

        // Parse constraints
        bool   isPrimaryKey = false;
        bool   nullable     = true;
        string description  = "";
        bool   isComputed   = false;
        string? expression  = null;
        bool   isPersisted  = false;

        if (constraintStr is not null)
        {
            foreach (var c in SplitConstraints(constraintStr))
            {
                var constraint = c.Trim();
                if (constraint.Equals("pk", StringComparison.OrdinalIgnoreCase) ||
                    constraint.Equals("primary key", StringComparison.OrdinalIgnoreCase))
                {
                    isPrimaryKey = true;
                    nullable     = false;
                }
                else if (constraint.Equals("not null", StringComparison.OrdinalIgnoreCase))
                {
                    nullable = false;
                }
                else if (constraint.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    nullable = true;
                }
                else if (constraint.StartsWith("note:", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = constraint["note:".Length..].Trim();
                    var val = UnquoteString(raw) ?? raw;
                    (description, isComputed, expression, isPersisted) = ParseNoteValue(val);
                }
                // unique, increment, default, ref — silently skipped
            }
        }

        return new FieldDefinition
        {
            Name               = name,
            Type               = type,
            Nullable           = nullable,
            IsPrimaryKey       = isPrimaryKey,
            Description        = description,
            IsComputed         = isComputed,
            ComputedExpression = expression,
            IsPersisted        = isPersisted,
        };
    }

    private static (string Description, bool IsComputed, string? Expression, bool IsPersisted)
        ParseNoteValue(string note)
    {
        const string marker = "// calculated:";
        var idx = note.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (note, false, null, false);

        var descPart = note[..idx].TrimEnd(';', ' ', '\t');
        var calcPart = note[(idx + marker.Length)..].Trim();

        bool isPersisted = false;
        if (calcPart.EndsWith(" PERSISTED", StringComparison.OrdinalIgnoreCase))
        {
            isPersisted = true;
            calcPart    = calcPart[..^" PERSISTED".Length].Trim();
        }

        // Strip outer parentheses added by the generator
        if (calcPart.StartsWith('(') && calcPart.EndsWith(')'))
            calcPart = calcPart[1..^1];

        return (descPart, true, calcPart, isPersisted);
    }

    // ── Ref line ──────────────────────────────────────────────────────────────

    private sealed record PendingRef(
        string SourceTable, string SourceField,
        string TargetTable, string TargetField,
        ForeignKeyKind Kind);

    private static PendingRef? ParseRef(string line, string? schemaPrefix)
    {
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return null;
        var body = line[(colonIdx + 1)..].Trim();

        // Strip trailing comment, but only outside quoted strings
        string? comment = null;
        var commentIdx = FindOuterCommentStart(body);
        if (commentIdx >= 0)
        {
            comment = body[(commentIdx + 2)..].Trim();
            body    = body[..commentIdx].TrimEnd();
        }

        var kind = comment?.ToLowerInvariant() switch
        {
            "logical" => ForeignKeyKind.Logical,
            "virtual" => ForeignKeyKind.Virtual,
            _         => ForeignKeyKind.Physical,
        };

        // Determine direction: check <> before < and >
        string leftStr, rightStr, direction;

        if (TryFindOp(body, " <> ", out int opIdx))        { direction = "<>"; leftStr = body[..opIdx].Trim(); rightStr = body[(opIdx + 4)..].Trim(); }
        else if (TryFindOp(body, " > ",  out opIdx))       { direction = ">";  leftStr = body[..opIdx].Trim(); rightStr = body[(opIdx + 3)..].Trim(); }
        else if (TryFindOp(body, " < ",  out opIdx))       { direction = "<";  leftStr = body[..opIdx].Trim(); rightStr = body[(opIdx + 3)..].Trim(); }
        else if (TryFindOp(body, " - ",  out opIdx))       { direction = ">";  leftStr = body[..opIdx].Trim(); rightStr = body[(opIdx + 3)..].Trim(); }
        else return null;

        if (direction == "<>") return null; // many-to-many not supported

        var (leftTable, leftField) = SplitTableField(leftStr);
        var (rightTable, rightField) = SplitTableField(rightStr);

        if (leftTable is null || leftField is null || rightTable is null || rightField is null)
            return null;

        // Apply schema prefix to unqualified names
        if (!string.IsNullOrEmpty(schemaPrefix))
        {
            if (!leftTable.Contains('.'))  leftTable  = $"{schemaPrefix}.{leftTable}";
            if (!rightTable.Contains('.')) rightTable = $"{schemaPrefix}.{rightTable}";
        }

        // direction ">" → left has FK pointing to right
        // direction "<" → right has FK pointing to left
        string sourceTable, sourceField, targetTable, targetField;
        if (direction == ">")
        {
            (sourceTable, sourceField, targetTable, targetField) = (leftTable, leftField, rightTable, rightField);
        }
        else
        {
            (sourceTable, sourceField, targetTable, targetField) = (rightTable, rightField, leftTable, leftField);
        }

        return new PendingRef(sourceTable, sourceField, targetTable, targetField, kind);
    }

    // ── TableGroup block ──────────────────────────────────────────────────────

    private sealed record PendingGroup(string Name, IReadOnlyList<string> Tables);

    private static (PendingGroup Group, int EndLine) ParseTableGroup(string[] lines, int startLine)
    {
        var header    = lines[startLine].Trim();
        var groupName = ExtractBlockName(header, "TableGroup ") ?? "Unnamed";

        var tableNames = new List<string>();
        int depth = 0, endLine = startLine;

        for (int i = startLine; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Contains('{')) depth++;
            if (t.Contains('}')) { depth--; if (depth == 0) { endLine = i; break; } }
            if (i == startLine || depth != 1) continue;
            if (!string.IsNullOrWhiteSpace(t) && !t.StartsWith("//"))
                tableNames.Add(t.Trim('"'));
        }

        return (new PendingGroup(groupName, tableNames), endLine);
    }

    // ── Low-level helpers ─────────────────────────────────────────────────────

    private static string StripLineComment(string line)
    {
        var bracketIdx = line.IndexOf('[');
        if (bracketIdx < 0)
        {
            var ci = line.IndexOf("//", StringComparison.Ordinal);
            return ci >= 0 ? line[..ci].TrimEnd() : line;
        }

        var commentBeforeBracket = line.IndexOf("//", StringComparison.Ordinal);
        if (commentBeforeBracket >= 0 && commentBeforeBracket < bracketIdx)
            return line[..commentBeforeBracket].TrimEnd();

        var closeBracket = FindClosingBracket(line, bracketIdx);
        if (closeBracket >= 0)
        {
            var after       = line[(closeBracket + 1)..];
            var trailingCi  = after.IndexOf("//", StringComparison.Ordinal);
            if (trailingCi >= 0)
                return line[..(closeBracket + 1 + trailingCi)].TrimEnd();
        }

        return line;
    }

    private static int FindOuterCommentStart(string s)
    {
        bool inString = false;
        for (int i = 0; i < s.Length - 1; i++)
        {
            if (s[i] == '"' && (i == 0 || s[i - 1] != '\\')) inString = !inString;
            if (!inString && s[i] == '/' && s[i + 1] == '/') return i;
        }
        return -1;
    }

    private static int FindClosingBracket(string s, int openIdx)
    {
        int depth = 0;
        bool inStr = false;
        for (int i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '"' && (i == 0 || s[i - 1] != '\\')) inStr = !inStr;
            if (!inStr)
            {
                if (s[i] == '[') depth++;
                if (s[i] == ']') { depth--; if (depth == 0) return i; }
            }
        }
        return -1;
    }

    private static int FindClosingQuote(string s, int openIdx)
    {
        for (int i = openIdx + 1; i < s.Length; i++)
        {
            if (s[i] == '"' && s[i - 1] != '\\') return i;
        }
        return -1;
    }

    private static List<string> SplitConstraints(string s)
    {
        var result  = new List<string>();
        var current = new StringBuilder();
        bool inStr  = false;

        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"' && (i == 0 || s[i - 1] != '\\')) inStr = !inStr;
            if (c == '\'' && !inStr)
            {
                current.Append(c);
                continue;
            }
            if (c == ',' && !inStr)
            {
                var tok = current.ToString().Trim();
                if (tok.Length > 0) result.Add(tok);
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        var last = current.ToString().Trim();
        if (last.Length > 0) result.Add(last);
        return result;
    }

    private static string? UnquoteString(string s)
    {
        s = s.Trim();
        if (s.StartsWith("'''") && s.EndsWith("'''")) return s[3..^3];
        if (s.StartsWith('\'') && s.EndsWith('\''))   return s[1..^1].Replace("\\'", "'");
        if (s.StartsWith('"') && s.EndsWith('"'))
            return s[1..^1]
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n",  "\n");
        return null;
    }

    private static string? ExtractNoteValue(string line)
    {
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return null;
        return UnquoteString(line[(colonIdx + 1)..].Trim()) ?? line[(colonIdx + 1)..].Trim();
    }

    private static string? ExtractBlockName(string line, string prefix)
    {
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = line[prefix.Length..].Trim();

        // Remove trailing { (and anything after it on this line)
        var braceIdx = rest.IndexOf('{');
        if (braceIdx >= 0) rest = rest[..braceIdx].Trim();

        if (rest.StartsWith('"') && rest.EndsWith('"')) return rest[1..^1];
        return rest;
    }

    private static bool TryFindOp(string s, string op, out int index)
    {
        bool inStr = false;
        for (int i = 0; i <= s.Length - op.Length; i++)
        {
            if (s[i] == '"' && (i == 0 || s[i - 1] != '\\')) inStr = !inStr;
            if (!inStr && s.AsSpan(i, op.Length).Equals(op.AsSpan(), StringComparison.Ordinal))
            {
                index = i;
                return true;
            }
        }
        index = -1;
        return false;
    }

    private static (string? Table, string? Field) SplitTableField(string s)
    {
        s = s.Trim();

        int lastDot = -1;
        bool inStr  = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') inStr = !inStr;
            if (s[i] == '.' && !inStr) lastDot = i;
        }

        if (lastDot < 0) return (null, null);

        var table = s[..lastDot].Trim('"');
        var field = s[(lastDot + 1)..].Trim('"');
        return (table, field);
    }
}
