using System.Text.RegularExpressions;
using Manifesta.Core.IR;

namespace Manifesta.Core;

/// <summary>
/// Parses a Prisma schema file (.prisma) and extracts Manifesta <see cref="TableDefinition"/> instances.
/// Uses a two-pass approach:
/// <list type="bullet">
///   <item>Pass 1 — collects model/enum names, datasource settings, and model→table-name mappings (via @@map).</item>
///   <item>Pass 2 — parses each model/enum block into a <see cref="TableDefinition"/>.</item>
/// </list>
/// </summary>
public sealed class PrismaParser
{
    // ── Scalar type map: Prisma type → SQL type [SqlServer=0, MySql=1, Postgres=2] ─────

    private static readonly Dictionary<string, string[]> ScalarMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["String"]   = ["nvarchar(max)",  "varchar(191)",  "text"],
            ["Int"]      = ["int",            "int",           "integer"],
            ["BigInt"]   = ["bigint",         "bigint",        "bigint"],
            ["Float"]    = ["float",          "double",        "double precision"],
            ["Decimal"]  = ["decimal(18,6)",  "decimal(18,6)", "decimal(65,30)"],
            ["Boolean"]  = ["bit",            "tinyint(1)",    "boolean"],
            ["DateTime"] = ["datetime2",      "datetime",      "timestamp"],
            ["Bytes"]    = ["varbinary(max)", "longblob",      "bytea"],
            ["Json"]     = ["nvarchar(max)",  "json",          "jsonb"],
        };

    // ── Entry point ──────────────────────────────────────────────────────────────────

    public PrismaParseResult Parse(
        string      schema,
        DbProvider? providerOverride = null,
        string?     schemaPrefix     = null,
        bool        includeEnums     = false)
    {
        var lines  = schema.ReplaceLineEndings("\n").Split('\n');
        var errors = new List<string>();
        var tables = new List<TableDefinition>();
        var enums  = new List<TableDefinition>();

        // ── Pass 1: collect names, datasource settings, @@map overrides ──────────────
        var modelNames   = new HashSet<string>(StringComparer.Ordinal);
        var enumNames    = new HashSet<string>(StringComparer.Ordinal);
        var modelToTable = new Dictionary<string, string>(StringComparer.Ordinal);
        DbProvider? detected = null;
        bool        isFkMode = true;

        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();

            if (t.StartsWith("model ", StringComparison.Ordinal))
            {
                var name = BlockName(t);
                if (name == null) continue;
                modelNames.Add(name);
                var mapped = ScanForMap(lines, i + 1);
                modelToTable[name] = mapped ?? name;
            }
            else if (t.StartsWith("enum ", StringComparison.Ordinal))
            {
                var name = BlockName(t);
                if (name != null) enumNames.Add(name);
            }
            else if (t.StartsWith("datasource ", StringComparison.Ordinal) && detected == null)
            {
                i++;
                while (i < lines.Length)
                {
                    var dl = lines[i].Trim();
                    if (dl == "}") break;
                    if (dl.StartsWith("provider", StringComparison.OrdinalIgnoreCase))
                        detected = ParseProvider(ExtractQuotedValue(dl));
                    else if (dl.StartsWith("relationMode", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = ExtractQuotedValue(dl);
                        isFkMode = v?.Equals("foreignKeys", StringComparison.OrdinalIgnoreCase) ?? true;
                    }
                    i++;
                }
            }
        }

        var provider    = providerOverride ?? detected ?? DbProvider.SqlServer;
        var fkKind      = isFkMode ? ForeignKeyKind.Physical : ForeignKeyKind.Logical;
        var providerIdx = provider switch
        {
            DbProvider.MySql    => 1,
            DbProvider.Postgres => 2,
            _                   => 0
        };

        // ── Pass 2: parse each model/enum block ──────────────────────────────────────
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();

            if (t.StartsWith("model ", StringComparison.Ordinal))
            {
                var name  = BlockName(t);
                var block = ReadBlock(lines, ref i);
                if (name == null) continue;

                var td = ParseModelBlock(name, block, modelNames, enumNames, modelToTable,
                                         providerIdx, fkKind, schemaPrefix, errors);
                if (td != null) tables.Add(td);
            }
            else if (t.StartsWith("enum ", StringComparison.Ordinal))
            {
                var name  = BlockName(t);
                var block = ReadBlock(lines, ref i);
                if (name == null || !includeEnums) continue;

                var td = ParseEnumBlock(name, providerIdx, schemaPrefix);
                if (td != null) enums.Add(td);
            }
        }

        return new PrismaParseResult(tables, enums, errors);
    }

    // ── Model block parser ───────────────────────────────────────────────────────────

    private static TableDefinition? ParseModelBlock(
        string                     prismaName,
        IReadOnlyList<string>      lines,
        HashSet<string>            modelNames,
        HashSet<string>            enumNames,
        Dictionary<string, string> modelToTable,
        int                        providerIdx,
        ForeignKeyKind             fkKind,
        string?                    schemaPrefix,
        List<string>               errors)
    {
        var fields            = new List<FieldDefinition>();
        var primaryKeys       = new List<string>();
        var foreignKeys       = new List<ForeignKey>();
        var indexes           = new List<IndexDefinition>();
        var uniqueConstraints = new List<UniqueConstraint>();
        string? tableMap      = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;

            if (line.StartsWith("@@"))
            {
                if (line.StartsWith("@@map("))
                    tableMap = ExtractQuotedValue(line);
                else if (line.StartsWith("@@id("))
                {
                    foreach (var c in ExtractBracketList(line))
                        if (!primaryKeys.Contains(c)) primaryKeys.Add(c);
                }
                else if (line.StartsWith("@@unique("))
                {
                    var cols = ExtractBracketList(line);
                    if (cols.Count > 0)
                        uniqueConstraints.Add(new UniqueConstraint
                        {
                            Name    = $"uq_{string.Join("_", cols).ToLowerInvariant()}",
                            Columns = cols,
                        });
                }
                else if (line.StartsWith("@@index("))
                {
                    var idx = ParseIndexAttribute(line);
                    if (idx != null) indexes.Add(idx);
                }
                continue;
            }

            var (fieldName, prismaType, attrs) = SplitFieldLine(line);
            if (string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(prismaType))
                continue;

            var isNullable = prismaType.EndsWith('?');
            var isArray    = prismaType.EndsWith("[]");
            var baseType   = prismaType.TrimEnd('?', '[', ']');

            if (isArray || modelNames.Contains(baseType))
            {
                if (!isArray)
                {
                    var fk = TryExtractRelationFk(attrs, baseType, modelToTable, fkKind, schemaPrefix);
                    if (fk != null) foreignKeys.Add(fk);
                }
                continue;
            }

            bool isId = HasAttr(attrs, "@id");
            if (isId && !primaryKeys.Contains(fieldName))
                primaryKeys.Add(fieldName);

            string? defaultVal = ExtractDefault(attrs);
            string? nativeSql  = ExtractNativeType(attrs);

            string sqlType;
            if (nativeSql != null)
            {
                sqlType = nativeSql;
            }
            else if (baseType.StartsWith("Unsupported(", StringComparison.OrdinalIgnoreCase))
            {
                sqlType = ExtractQuotedValue(baseType) ?? baseType;
            }
            else if (enumNames.Contains(baseType))
            {
                sqlType = providerIdx switch { 1 => "varchar(191)", 2 => "text", _ => "nvarchar(max)" };
            }
            else if (ScalarMap.TryGetValue(baseType, out var types))
            {
                sqlType = types[providerIdx];
            }
            else
            {
                errors.Add($"Unknown Prisma type '{baseType}' on field '{fieldName}' — used as-is.");
                sqlType = baseType;
            }

            if (HasAttr(attrs, "@unique"))
            {
                uniqueConstraints.Add(new UniqueConstraint
                {
                    Name    = $"uq_{fieldName.ToLowerInvariant()}",
                    Columns = [fieldName],
                });
            }

            fields.Add(new FieldDefinition
            {
                Name         = fieldName,
                Type         = sqlType,
                Nullable     = isNullable && !isId,
                Default      = defaultVal,
                IsPrimaryKey = isId,
            });
        }

        var tableName = BuildTableName(tableMap ?? prismaName, schemaPrefix);

        return new TableDefinition
        {
            Name              = tableName,
            Fields            = fields,
            PrimaryKey        = primaryKeys,
            ForeignKeys       = foreignKeys,
            Indexes           = indexes,
            UniqueConstraints = uniqueConstraints,
        };
    }

    // ── Enum block parser ────────────────────────────────────────────────────────────

    private static TableDefinition ParseEnumBlock(string prismaName, int providerIdx, string? schemaPrefix)
    {
        var valueType = providerIdx switch { 1 => "varchar(191)", 2 => "text", _ => "nvarchar(max)" };
        return new TableDefinition
        {
            Name             = BuildTableName(prismaName, schemaPrefix),
            Fields           = [new FieldDefinition { Name = "Value", Type = valueType, Nullable = false }],
            IsReferenceTable = true,
        };
    }

    // ── Block / line parsing helpers ─────────────────────────────────────────────────

    private static string? BlockName(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1].TrimEnd('{').Trim() : null;
    }

    private static List<string> ReadBlock(string[] lines, ref int i)
    {
        i++;
        var result = new List<string>();
        while (i < lines.Length)
        {
            if (lines[i].Trim() == "}") break;
            result.Add(lines[i]);
            i++;
        }
        return result;
    }

    private static string? ScanForMap(string[] lines, int startIdx)
    {
        for (int i = startIdx; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t == "}") break;
            if (t.StartsWith("@@map(", StringComparison.Ordinal))
                return ExtractQuotedValue(t);
        }
        return null;
    }

    private static string BuildTableName(string name, string? schemaPrefix)
        => string.IsNullOrWhiteSpace(schemaPrefix) ? name : $"{schemaPrefix}.{name}";

    private static DbProvider? ParseProvider(string? val) =>
        val?.ToLowerInvariant() switch
        {
            "sqlserver" or "mssql" => DbProvider.SqlServer,
            "mysql"                => DbProvider.MySql,
            "postgresql" or "postgres" => DbProvider.Postgres,
            _ => null
        };

    private static (string Name, string Type, string Attrs) SplitFieldLine(string line)
    {
        var nameEnd = IndexOfWhitespace(line, 0);
        if (nameEnd < 0) return (line, "", "");
        var name = line[..nameEnd];

        var typeStart = SkipSpaces(line, nameEnd);
        if (typeStart >= line.Length) return (name, "", "");
        var typeEnd = IndexOfWhitespace(line, typeStart);
        var type    = typeEnd < 0 ? line[typeStart..] : line[typeStart..typeEnd];

        var attrStart = typeEnd >= 0 ? SkipSpaces(line, typeEnd) : line.Length;
        var attrs     = attrStart < line.Length ? line[attrStart..] : "";

        return (name, type, attrs);
    }

    private static int IndexOfWhitespace(string s, int start)
    {
        for (int i = start; i < s.Length; i++)
            if (char.IsWhiteSpace(s[i])) return i;
        return -1;
    }

    private static int SkipSpaces(string s, int start)
    {
        while (start < s.Length && char.IsWhiteSpace(s[start])) start++;
        return start;
    }

    private static bool HasAttr(string attrs, string attr)
    {
        int idx = 0;
        while (true)
        {
            idx = attrs.IndexOf(attr, idx, StringComparison.Ordinal);
            if (idx < 0) return false;
            if (idx > 0 && attrs[idx - 1] == '@') { idx++; continue; }
            var after = idx + attr.Length;
            if (after < attrs.Length && (char.IsLetterOrDigit(attrs[after]) || attrs[after] == '_'))
            { idx++; continue; }
            return true;
        }
    }

    private static string? ExtractQuotedValue(string s)
    {
        var q1 = s.IndexOf('"');
        if (q1 < 0) return null;
        var q2 = s.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        return s[(q1 + 1)..q2];
    }

    private static List<string> ExtractBracketList(string line)
    {
        var s = line.IndexOf('[');
        if (s < 0) return [];
        var e = line.IndexOf(']', s);
        if (e < 0) return [];
        return [.. line[(s + 1)..e].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
    }

    private static IndexDefinition? ParseIndexAttribute(string line)
    {
        var cols = ExtractBracketList(line);
        if (cols.Count == 0) return null;

        string? name = null;
        var nm = Regex.Match(line, @"name\s*:\s*""([^""]+)""");
        if (nm.Success) name = nm.Groups[1].Value;
        name ??= $"idx_{string.Join("_", cols.Select(c => c.ToLowerInvariant()))}";

        return new IndexDefinition
        {
            Name        = name,
            Columns     = cols,
            IsUnique    = false,
            IsClustered = false,
            IsFiltered  = false,
        };
    }

    // ── Attribute extraction ─────────────────────────────────────────────────────────

    private static string? ExtractDefault(string attrs)
    {
        var idx = attrs.IndexOf("@default(", StringComparison.Ordinal);
        if (idx < 0) return null;
        if (idx > 0 && attrs[idx - 1] == '@') return null;

        var inner = ExtractParenContent(attrs, idx + 8);
        if (inner == null) return null;

        if (Regex.IsMatch(inner, @"^(autoincrement|cuid|uuid|dbgenerated|now)\s*\(", RegexOptions.IgnoreCase))
            return null;

        if (inner.StartsWith('"') && inner.EndsWith('"') && inner.Length >= 2)
            return inner[1..^1];

        var dot = inner.IndexOf('.');
        if (dot >= 0) return inner[(dot + 1)..];

        return inner;
    }

    private static string? ExtractNativeType(string attrs)
    {
        var idx = attrs.IndexOf("@db.", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var nameStart = idx + 4;
        var nameEnd   = nameStart;
        while (nameEnd < attrs.Length && (char.IsLetterOrDigit(attrs[nameEnd]) || attrs[nameEnd] == '_'))
            nameEnd++;

        var typeName = attrs[nameStart..nameEnd];
        string? args = null;
        if (nameEnd < attrs.Length && attrs[nameEnd] == '(')
            args = ExtractParenContent(attrs, nameEnd);

        return NormalizeNativeType(typeName, args);
    }

    private static string NormalizeNativeType(string typeName, string? args)
    {
        return typeName.ToLowerInvariant() switch
        {
            "varchar"          => args != null ? $"varchar({args})" : "varchar",
            "nvarchar"         => args != null ? $"nvarchar({args})" : "nvarchar",
            "char"             => args != null ? $"char({args})" : "char",
            "nchar"            => args != null ? $"nchar({args})" : "nchar",
            "text"             => "text",
            "ntext"            => "ntext",
            "tinytext"         => "tinytext",
            "mediumtext"       => "mediumtext",
            "longtext"         => "longtext",
            "tinyint"          => "tinyint",
            "smallint"         => "smallint",
            "mediumint"        => "mediumint",
            "int"              => "int",
            "unsignedint"      => "int unsigned",
            "bigint"           => "bigint",
            "decimal"          => args != null ? $"decimal({args})" : "decimal",
            "double"           => "double",
            "doubleprecision"  => "double precision",
            "float"            => "float",
            "real"             => "real",
            "money"            => "money",
            "smallmoney"       => "smallmoney",
            "bit"              => "bit",
            "boolean"          => "boolean",
            "date"             => "date",
            "time"             => args != null ? $"time({args})" : "time",
            "datetime"         => "datetime",
            "datetime2"        => args != null ? $"datetime2({args})" : "datetime2",
            "datetimeoffset"   => args != null ? $"datetimeoffset({args})" : "datetimeoffset",
            "smalldatetime"    => "smalldatetime",
            "timestamp"        => "timestamp",
            "uniqueidentifier" => "uniqueidentifier",
            "xml"              => "xml",
            "json"             => "json",
            "jsonb"            => "jsonb",
            "uuid"             => "uuid",
            "inet"             => "inet",
            "cidr"             => "cidr",
            "objectid"         => "objectId",
            "bytes"            => "bytes",
            "binary"           => args != null ? $"binary({args})" : "binary",
            "varbinary"        => args != null ? $"varbinary({args})" : "varbinary",
            "longblob"         => "longblob",
            "mediumblob"       => "mediumblob",
            "blob"             => "blob",
            "tinyblob"         => "tinyblob",
            "bytea"            => "bytea",
            _                  => typeName,
        };
    }

    private static ForeignKey? TryExtractRelationFk(
        string                     attrs,
        string                     targetModelName,
        Dictionary<string, string> modelToTable,
        ForeignKeyKind             fkKind,
        string?                    schemaPrefix)
    {
        var idx = attrs.IndexOf("@relation(", StringComparison.Ordinal);
        if (idx < 0) return null;
        if (idx > 0 && attrs[idx - 1] == '@') return null;

        var inner = ExtractParenContent(attrs, idx + 9);
        if (inner == null) return null;

        var fields = ExtractRelationList(inner, "fields");
        var refs   = ExtractRelationList(inner, "references");

        if (fields == null || refs == null || fields.Count == 0 || refs.Count == 0)
            return null;

        if (fields.Count > 1) return null;

        bool cascade = false;
        var m = Regex.Match(inner, @"onDelete\s*:\s*(\w+)", RegexOptions.IgnoreCase);
        if (m.Success) cascade = m.Groups[1].Value.Equals("Cascade", StringComparison.OrdinalIgnoreCase);

        var targetTable = modelToTable.TryGetValue(targetModelName, out var mapped) ? mapped : targetModelName;
        targetTable = BuildTableName(targetTable, schemaPrefix);

        return new ForeignKey
        {
            SourceField   = fields[0],
            TargetTable   = targetTable,
            TargetField   = refs[0],
            CascadeDelete = cascade,
            Kind          = fkKind,
        };
    }

    private static List<string>? ExtractRelationList(string inner, string argName)
    {
        var m = Regex.Match(inner, argName + @"\s*:\s*\[([^\]]*)\]", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return [.. m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
    }

    private static string? ExtractParenContent(string s, int openParenPos)
    {
        if (openParenPos >= s.Length || s[openParenPos] != '(') return null;
        int depth = 0;
        var start = openParenPos + 1;
        for (int i = openParenPos; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') { depth--; if (depth == 0) return s[start..i]; }
        }
        return null;
    }
}
