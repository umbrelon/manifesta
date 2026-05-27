using System.Text;
using System.Text.RegularExpressions;
using Manifesta.Core.IR;

namespace Manifesta.Core;

/// <summary>
/// Hand-rolled parser for SQL DDL <c>CREATE TABLE</c> statements.
/// Supports MySQL, PostgreSQL, SQLite, and SQL Server (T-SQL) dialects via the
/// provider argument passed to <see cref="Parse"/>.
///
/// Handles both clean DDL scripts and database dump files (<c>mysqldump --no-data</c>,
/// <c>pg_dump --schema-only</c>). Non-CREATE TABLE statements (SET, LOCK, DROP, INSERT,
/// ALTER, GO, etc.) are silently skipped.
///
/// Covers the common subset required by <c>manifesta init sql</c>: columns with
/// types, nullability, defaults, PRIMARY KEY, FOREIGN KEY, UNIQUE, CHECK constraints,
/// computed columns, and inline index definitions.
/// ALTER TABLE and standalone CREATE INDEX are not parsed (v1).
/// </summary>
public sealed class SqlDdlParser
{
    // ── Public API ─────────────────────────────────────────────────────────────

    public sealed record ParseResult(
        IReadOnlyList<TableDefinition> Tables,
        IReadOnlyList<string>          Errors)
    {
        /// <summary>
        /// Primary-key column lists extracted from <c>ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY</c>
        /// statements found in the same SQL text. The caller is responsible for applying these to
        /// matching <see cref="Tables"/> entries that have an empty <see cref="TableDefinition.PrimaryKey"/>.
        /// </summary>
        public IReadOnlyList<AlterTablePkAddition> PkAdditions { get; init; } = [];
    }

    /// <summary>
    /// Primary-key information extracted from an <c>ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY</c>
    /// statement.  Used to enrich tables whose CREATE TABLE body omits the PK constraint.
    /// </summary>
    public sealed record AlterTablePkAddition(
        string TableName,
        IReadOnlyList<string> Columns);

    /// <summary>
    /// Parses one or more CREATE TABLE statements from <paramref name="sql"/>.
    /// </summary>
    /// <param name="sql">Full DDL text (script or dump file).</param>
    /// <param name="provider">Dialect for type normalisation and syntax quirks.</param>
    /// <param name="schemaPrefix">
    /// When set, unqualified table names are prefixed with this schema
    /// (e.g. <c>"dbo"</c> → <c>"dbo.Customer"</c>).
    /// </param>
    public ParseResult Parse(string sql, DbProvider provider, string? schemaPrefix = null)
    {
        var tables = new List<TableDefinition>();
        var errors = new List<string>();

        foreach (var block in ExtractCreateTableBlocks(sql))
        {
            var (table, blockErrors) = ParseCreateTable(block, provider, schemaPrefix);
            if (table is not null)
                tables.Add(table);
            errors.AddRange(blockErrors);
        }

        var pkAdditions = ExtractAlterTablePks(sql, schemaPrefix);
        return new ParseResult(tables.AsReadOnly(), errors.AsReadOnly())
        {
            PkAdditions = pkAdditions.AsReadOnly(),
        };
    }

    // ── Phase 0: extract ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY ──────────

    private static readonly Regex s_alterTableRx = new(
        @"ALTER\s+TABLE\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Scans <paramref name="sql"/> for <c>ALTER TABLE … ADD [CONSTRAINT name] PRIMARY KEY … (cols)</c>
    /// statements and returns one <see cref="AlterTablePkAddition"/> per match.
    /// Handles optional <c>IF NOT EXISTS (…)</c> guards and <c>CLUSTERED/NONCLUSTERED</c> keywords.
    /// </summary>
    private static List<AlterTablePkAddition> ExtractAlterTablePks(string sql, string? schemaPrefix)
    {
        var result = new List<AlterTablePkAddition>();
        var clean  = RemoveComments(sql);
        var tokens = Tokenize(clean);
        int i = 0;

        while (i < tokens.Count)
        {
            if (!tokens[i].Equals("ALTER", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            if (i + 1 >= tokens.Count || !tokens[i + 1].Equals("TABLE", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            i += 2; // consume ALTER TABLE

            if (i >= tokens.Count) break;
            var tableName = ReadQualifiedTableRef(tokens, ref i);
            if (string.IsNullOrWhiteSpace(tableName)) continue;

            // Apply schema prefix to unqualified names — same rule as CREATE TABLE
            if (!tableName.Contains('.') && schemaPrefix is not null)
                tableName = $"{schemaPrefix}.{tableName}";

            // Must be followed by ADD
            if (i >= tokens.Count || !tokens[i].Equals("ADD", StringComparison.OrdinalIgnoreCase)) continue;
            i++;

            // Optional CONSTRAINT <name>
            if (i < tokens.Count && tokens[i].Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
            {
                i++; // skip CONSTRAINT
                if (i < tokens.Count && !IsConstraintTypeKeyword(tokens[i]))
                    i++; // skip constraint name
            }

            // Must have PRIMARY KEY
            if (i >= tokens.Count || !tokens[i].Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= tokens.Count || !tokens[i + 1].Equals("KEY", StringComparison.OrdinalIgnoreCase)) continue;
            i += 2; // consume PRIMARY KEY

            // Optional CLUSTERED / NONCLUSTERED
            if (i < tokens.Count &&
                (tokens[i].Equals("CLUSTERED",    StringComparison.OrdinalIgnoreCase) ||
                 tokens[i].Equals("NONCLUSTERED", StringComparison.OrdinalIgnoreCase)))
                i++;

            // Column list
            if (i < tokens.Count && tokens[i].StartsWith('('))
            {
                var cols = ParseColumnList(tokens[i++]);
                if (cols.Count > 0)
                    result.Add(new AlterTablePkAddition(tableName, cols.AsReadOnly()));
            }
        }

        return result;
    }

    // ── Phase 1: extract CREATE TABLE blocks ──────────────────────────────────

    private static readonly Regex s_createTableRx = new(
        @"CREATE\s+(?:TEMPORARY\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IEnumerable<string> ExtractCreateTableBlocks(string sql)
    {
        var clean = RemoveComments(sql);

        int searchFrom = 0;
        while (true)
        {
            var m = s_createTableRx.Match(clean, searchFrom);
            if (!m.Success) yield break;

            // Find the opening paren of the column list
            int parenOpen = clean.IndexOf('(', m.Index + m.Length);
            if (parenOpen < 0) yield break;

            int parenClose = FindMatchingCloseParen(clean, parenOpen);
            if (parenClose < 0) yield break;

            yield return clean[m.Index..(parenClose + 1)];
            searchFrom = parenClose + 1;
        }
    }

    /// <summary>
    /// Removes block comments (<c>/* … */</c> including MySQL <c>/*!…*/</c>) and
    /// line comments (<c>--</c> and MySQL <c>#</c>). String literals are preserved.
    /// </summary>
    private static string RemoveComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        int i  = 0;

        while (i < sql.Length)
        {
            // Block comment (including MySQL /*!...*/  conditional comments)
            if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                    i++;
                if (i + 1 < sql.Length) i += 2;
                sb.Append(' ');
                continue;
            }

            // Line comment: --
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                continue;
            }

            // MySQL # line comment
            if (sql[i] == '#')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                continue;
            }

            // Single-quoted string literal — preserve including '' escapes
            if (sql[i] == '\'')
            {
                sb.Append(sql[i++]);
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        sb.Append(sql[i++]);
                        if (i < sql.Length && sql[i] == '\'') // '' escape
                            sb.Append(sql[i++]);
                        else
                            break;
                    }
                    else
                    {
                        sb.Append(sql[i++]);
                    }
                }
                continue;
            }

            sb.Append(sql[i++]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the index of the <c>)</c> that closes the <c>(</c> at
    /// <paramref name="openIdx"/>. Handles nested parens and string literals.
    /// Returns <c>-1</c> if unmatched.
    /// </summary>
    private static int FindMatchingCloseParen(string text, int openIdx)
    {
        int  depth    = 0;
        bool inString = false;

        for (int i = openIdx; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (c == '\'')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\'')
                        i++; // '' escape
                    else
                        inString = false;
                }
                continue;
            }

            if (c == '\'') { inString = true;                       continue; }
            if (c == '(')  { depth++;                               continue; }
            if (c == ')')  { depth--; if (depth == 0) return i; }
        }

        return -1;
    }

    // ── Phase 2: parse one CREATE TABLE block ─────────────────────────────────

    private static (TableDefinition? table, IReadOnlyList<string> errors) ParseCreateTable(
        string block, DbProvider provider, string? schemaPrefix)
    {
        var errors = new List<string>();

        int parenOpen  = block.IndexOf('(');
        int parenClose = parenOpen >= 0 ? FindMatchingCloseParen(block, parenOpen) : -1;

        if (parenOpen < 0 || parenClose < 0)
        {
            errors.Add($"Could not find column body in: {Truncate(block)}");
            return (null, errors);
        }

        var header = block[..parenOpen];
        var body   = block[(parenOpen + 1)..parenClose];

        // Extract the table name from the header
        var nameMatch = s_createTableRx.Match(header);
        if (!nameMatch.Success)
        {
            errors.Add($"Could not parse table name from: {Truncate(header)}");
            return (null, errors);
        }

        var rawName = header[(nameMatch.Index + nameMatch.Length)..].Trim();
        var (schema, tableName) = ParseQualifiedName(rawName);

        if (string.IsNullOrWhiteSpace(tableName))
        {
            errors.Add($"Empty table name in: {Truncate(header)}");
            return (null, errors);
        }

        var fullName = schema       is not null ? $"{schema}.{tableName}"
                     : schemaPrefix is not null ? $"{schemaPrefix}.{tableName}"
                     : tableName;

        // Parse column and constraint entries from the body
        var fields       = new List<FieldDefinition>();
        var pkCols       = new List<string>();
        var fks          = new List<ForeignKey>();
        var uniqueCs     = new List<UniqueConstraint>();
        var checkCs      = new List<CheckConstraint>();
        int constraintSeq = 0;

        foreach (var entry in SplitBodyEntries(body))
        {
            var trimmed = entry.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (IsConstraintEntry(trimmed))
            {
                ParseConstraintEntry(
                    trimmed, fullName, provider, schemaPrefix,
                    pkCols, fks, uniqueCs, checkCs,
                    ref constraintSeq, errors);
            }
            else
            {
                var field = ParseColumnEntry(trimmed, pkCols, fks, provider, errors);
                if (field is not null)
                    fields.Add(field);
            }
        }

        // Merge inline PK markers into pkCols if no table-level PK constraint was found
        if (pkCols.Count == 0)
            pkCols.AddRange(fields.Where(f => f.IsPrimaryKey).Select(f => f.Name));

        var table = new TableDefinition
        {
            Name              = fullName,
            Fields            = fields.AsReadOnly(),
            PrimaryKey        = pkCols.AsReadOnly(),
            ForeignKeys       = fks.AsReadOnly(),
            UniqueConstraints = uniqueCs.AsReadOnly(),
            CheckConstraints  = checkCs.AsReadOnly(),
        };

        return (table, errors);
    }

    // ── Phase 3a: split body on commas at paren depth 0 ──────────────────────

    private static List<string> SplitBodyEntries(string body)
    {
        var entries = new List<string>();
        var current = new StringBuilder();
        int  depth    = 0;
        bool inString = false;

        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];

            if (inString)
            {
                current.Append(c);
                if (c == '\'')
                {
                    if (i + 1 < body.Length && body[i + 1] == '\'')
                        current.Append(body[++i]); // '' escape
                    else
                        inString = false;
                }
                continue;
            }

            if (c == '\'') { inString = true; current.Append(c); continue; }
            if (c == '(')  { depth++;         current.Append(c); continue; }
            if (c == ')')  { depth--;         current.Append(c); continue; }

            if (c == ',' && depth == 0)
            {
                var e = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(e)) entries.Add(e);
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        var last = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(last)) entries.Add(last);

        return entries;
    }

    // ── Phase 3b: classify entries ────────────────────────────────────────────

    private static bool IsConstraintEntry(string entry)
    {
        var tokens = Tokenize(entry);
        if (tokens.Count == 0) return false;

        int idx = 0;

        // Optional CONSTRAINT name prefix
        if (idx < tokens.Count &&
            tokens[idx].Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
        {
            idx++; // skip CONSTRAINT
            if (idx < tokens.Count && !IsConstraintTypeKeyword(tokens[idx]))
                idx++; // skip name
        }

        return idx < tokens.Count && IsConstraintTypeKeyword(tokens[idx]);
    }

    private static bool IsConstraintTypeKeyword(string t) =>
        t.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
        || t.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase)
        || t.Equals("UNIQUE",  StringComparison.OrdinalIgnoreCase)
        || t.Equals("CHECK",   StringComparison.OrdinalIgnoreCase)
        || t.Equals("KEY",     StringComparison.OrdinalIgnoreCase)
        || t.Equals("INDEX",   StringComparison.OrdinalIgnoreCase);

    // ── Phase 3c: parse a column entry ────────────────────────────────────────

    private static FieldDefinition? ParseColumnEntry(
        string        entry,
        List<string>  pkCols,
        List<ForeignKey> fks,
        DbProvider    provider,
        List<string>  errors)
    {
        var tokens = Tokenize(entry);
        if (tokens.Count < 1) return null;

        int i = 0;

        // Column name
        var colName = UnquoteIdentifier(tokens[i++]);
        if (string.IsNullOrWhiteSpace(colName)) return null;

        if (i >= tokens.Count) return null;

        // ── SQL Server computed column: name AS (expr) [PERSISTED] ────────────
        if (tokens[i].Equals("AS", StringComparison.OrdinalIgnoreCase))
        {
            i++;
            var expr = i < tokens.Count && tokens[i].StartsWith('(')
                ? tokens[i++][1..^1].Trim()
                : string.Empty;
            bool persisted = i < tokens.Count &&
                             tokens[i].Equals("PERSISTED", StringComparison.OrdinalIgnoreCase);

            return new FieldDefinition
            {
                Name               = colName,
                Type               = "computed",
                Nullable           = true,
                IsComputed         = true,
                ComputedExpression = string.IsNullOrEmpty(expr) ? null : expr,
                IsPersisted        = persisted,
            };
        }

        // ── Type ──────────────────────────────────────────────────────────────

        if (i >= tokens.Count) return null;

        // Base type token — unquote bracket/backtick/double-quote identifiers
        // (e.g. SQL Server writes [int], [nvarchar] in column type positions)
        var typeTokens = new List<string> { UnquoteIdentifier(tokens[i++]) };

        // Optional precision/scale group: VARCHAR(255), DECIMAL(10,2), NVARCHAR(MAX)
        if (i < tokens.Count && tokens[i].StartsWith('('))
            typeTokens.Add(tokens[i++]);

        // Known multi-word type completions (PostgreSQL-centric)
        if (i < tokens.Count)
        {
            var base0 = typeTokens[0].ToUpperInvariant();

            // DOUBLE PRECISION
            if (base0 == "DOUBLE" &&
                tokens[i].Equals("PRECISION", StringComparison.OrdinalIgnoreCase))
            {
                typeTokens.Add(tokens[i++]);
            }
            // CHARACTER VARYING [(n)] / BIT VARYING [(n)]
            else if ((base0 is "CHARACTER" or "BIT") &&
                     tokens[i].Equals("VARYING", StringComparison.OrdinalIgnoreCase))
            {
                typeTokens.Add(tokens[i++]);
                if (i < tokens.Count && tokens[i].StartsWith('('))
                    typeTokens.Add(tokens[i++]);
            }
            // TIMESTAMP [(n)] WITH/WITHOUT TIME ZONE  |  TIME WITH/WITHOUT TIME ZONE
            else if ((base0 is "TIMESTAMP" or "TIME") &&
                     (tokens[i].Equals("WITH",    StringComparison.OrdinalIgnoreCase) ||
                      tokens[i].Equals("WITHOUT", StringComparison.OrdinalIgnoreCase)))
            {
                typeTokens.Add(tokens[i++]); // WITH / WITHOUT
                if (i < tokens.Count &&
                    tokens[i].Equals("TIME", StringComparison.OrdinalIgnoreCase))
                {
                    typeTokens.Add(tokens[i++]);
                    if (i < tokens.Count &&
                        tokens[i].Equals("ZONE", StringComparison.OrdinalIgnoreCase))
                        typeTokens.Add(tokens[i++]);
                }
            }
        }

        // Join: words separated by spaces, but paren groups attach directly to
        // the preceding word (e.g. "int(11)" not "int (11)", "character varying(255)")
        var rawTypeSb = new StringBuilder(typeTokens[0]);
        for (int j = 1; j < typeTokens.Count; j++)
        {
            if (typeTokens[j].StartsWith('('))
                rawTypeSb.Append(typeTokens[j]);
            else
                rawTypeSb.Append(' ').Append(typeTokens[j]);
        }
        var rawType = rawTypeSb.ToString();

        // ── PostgreSQL GENERATED ALWAYS AS (expr) STORED ─────────────────────
        bool   isComputed   = false;
        string? computedExpr = null;
        bool   isPersisted  = false;

        if (i < tokens.Count &&
            tokens[i].Equals("GENERATED", StringComparison.OrdinalIgnoreCase))
        {
            // Consume: GENERATED [ALWAYS | BY DEFAULT] [AS IDENTITY | AS (expr) [STORED|VIRTUAL]]
            i++;
            // Skip ALWAYS / BY DEFAULT
            while (i < tokens.Count &&
                   !tokens[i].StartsWith('(') &&
                   !tokens[i].Equals("STORED",  StringComparison.OrdinalIgnoreCase) &&
                   !tokens[i].Equals("VIRTUAL", StringComparison.OrdinalIgnoreCase) &&
                   !tokens[i].Equals("IDENTITY", StringComparison.OrdinalIgnoreCase))
                i++;

            if (i < tokens.Count && tokens[i].Equals("IDENTITY", StringComparison.OrdinalIgnoreCase))
            {
                // GENERATED … AS IDENTITY — auto-increment, no expression
                i++;
                if (i < tokens.Count && tokens[i].StartsWith('('))
                    i++; // skip optional (seed, increment)
            }
            else if (i < tokens.Count && tokens[i].StartsWith('('))
            {
                computedExpr = tokens[i++][1..^1].Trim();
                isComputed   = true;
                if (i < tokens.Count &&
                    tokens[i].Equals("STORED", StringComparison.OrdinalIgnoreCase))
                {
                    isPersisted = true;
                    i++;
                }
                else if (i < tokens.Count &&
                         tokens[i].Equals("VIRTUAL", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                }
            }
        }

        // ── MySQL GENERATED ALWAYS AS (expr) [STORED|VIRTUAL] ────────────────
        // (same keyword sequence but without the STORED type qualifier from IDENTITY)
        // Handled by the same block above since the token sequence matches.

        // ── Modifiers ─────────────────────────────────────────────────────────
        bool    nullable    = true;
        string? defaultVal  = null;
        bool    isPk        = false;
        string? description = null;
        string? inlineFkTable = null;
        string? inlineFkCol   = null;
        bool    cascadeDelete = false;

        while (i < tokens.Count)
        {
            var tok = tokens[i];

            // NOT NULL
            if (tok.Equals("NOT", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < tokens.Count &&
                tokens[i + 1].Equals("NULL", StringComparison.OrdinalIgnoreCase))
            {
                nullable = false;
                i += 2;
                continue;
            }

            // NULL
            if (tok.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            {
                nullable = true;
                i++;
                continue;
            }

            // DEFAULT <value>
            if (tok.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                defaultVal = ExtractDefaultValue(tokens, ref i);
                continue;
            }

            // PRIMARY KEY (inline)
            if (tok.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < tokens.Count &&
                tokens[i + 1].Equals("KEY", StringComparison.OrdinalIgnoreCase))
            {
                isPk     = true;
                nullable = false;
                i += 2;
                continue;
            }

            // UNIQUE (inline) — noted but no field in IR; table-level UNIQUE constraints are preferred
            if (tok.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            // AUTO_INCREMENT / AUTOINCREMENT — strip
            if (tok.Equals("AUTO_INCREMENT",  StringComparison.OrdinalIgnoreCase) ||
                tok.Equals("AUTOINCREMENT",    StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            // IDENTITY [(seed, increment)] — SQL Server; strip
            if (tok.Equals("IDENTITY", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i < tokens.Count && tokens[i].StartsWith('('))
                    i++; // skip (seed, increment)
                continue;
            }

            // REFERENCES table [(col)] [ON DELETE CASCADE]
            if (tok.Equals("REFERENCES", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i < tokens.Count)
                {
                    inlineFkTable = ReadQualifiedTableRef(tokens, ref i);
                    if (i < tokens.Count && tokens[i].StartsWith('('))
                    {
                        var inner = tokens[i++][1..^1];
                        inlineFkCol = UnquoteIdentifier(
                            inner.Split(',')[0].Trim());
                    }
                }
                if (i + 2 < tokens.Count &&
                    tokens[i].Equals("ON",      StringComparison.OrdinalIgnoreCase) &&
                    tokens[i + 1].Equals("DELETE",  StringComparison.OrdinalIgnoreCase) &&
                    tokens[i + 2].Equals("CASCADE", StringComparison.OrdinalIgnoreCase))
                {
                    cascadeDelete = true;
                    i += 3;
                }
                continue;
            }

            // MySQL COMMENT 'text' → description
            if (tok.Equals("COMMENT", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i < tokens.Count)
                    description = UnquoteString(tokens[i++]);
                continue;
            }

            // CHARACTER SET / CHARSET / COLLATE charset_name — strip
            if (tok.Equals("CHARACTER",  StringComparison.OrdinalIgnoreCase) ||
                tok.Equals("CHARSET",    StringComparison.OrdinalIgnoreCase) ||
                tok.Equals("COLLATE",    StringComparison.OrdinalIgnoreCase))
            {
                i++;
                // Skip optional SET keyword
                if (i < tokens.Count &&
                    tokens[i].Equals("SET", StringComparison.OrdinalIgnoreCase))
                    i++;
                // Skip charset/collation name
                if (i < tokens.Count && !IsKnownModifier(tokens[i]))
                    i++;
                continue;
            }

            // VIRTUAL / STORED / PERSISTED (computed column qualifiers) — strip
            if (tok.Equals("VIRTUAL",   StringComparison.OrdinalIgnoreCase) ||
                tok.Equals("STORED",    StringComparison.OrdinalIgnoreCase) ||
                tok.Equals("PERSISTED", StringComparison.OrdinalIgnoreCase))
            {
                if (tok.Equals("PERSISTED", StringComparison.OrdinalIgnoreCase))
                    isPersisted = true;
                i++;
                continue;
            }

            // ON UPDATE <value> — MySQL timestamp auto-update; strip
            if (tok.Equals("ON", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < tokens.Count &&
                tokens[i + 1].Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                i += 2;
                if (i < tokens.Count && !IsKnownModifier(tokens[i]))
                    i++; // skip value
                continue;
            }

            // INVISIBLE / VISIBLE — MySQL 8; strip
            if (tok.Equals("INVISIBLE", StringComparison.OrdinalIgnoreCase) ||
                tok.Equals("VISIBLE",   StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            // CHECK (...) inline — skip for now (uncommon inline, handled as constraint)
            if (tok.Equals("CHECK", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i < tokens.Count && tokens[i].StartsWith('('))
                    i++;
                continue;
            }

            // Anything else — silently skip
            i++;
        }

        // Register inline FK
        if (inlineFkTable is not null)
        {
            fks.Add(new ForeignKey
            {
                SourceField   = colName,
                TargetTable   = inlineFkTable,
                TargetField   = inlineFkCol ?? "id",
                CascadeDelete = cascadeDelete,
                Kind          = ForeignKeyKind.Physical,
            });
        }

        // Register inline PK
        if (isPk)
            pkCols.Add(colName);

        return new FieldDefinition
        {
            Name               = colName,
            Type               = NormalizeType(rawType, provider),
            Nullable           = nullable,
            Default            = defaultVal,
            Description        = description ?? "",
            IsPrimaryKey       = isPk,
            IsComputed         = isComputed,
            ComputedExpression = computedExpr,
            IsPersisted        = isPersisted,
        };
    }

    // ── Phase 3d: parse a constraint entry ────────────────────────────────────

    private static void ParseConstraintEntry(
        string           entry,
        string           tableName,
        DbProvider       provider,
        string?          schemaPrefix,
        List<string>     pkCols,
        List<ForeignKey> fks,
        List<UniqueConstraint>  uniqueCs,
        List<CheckConstraint>   checkCs,
        ref int          seq,
        List<string>     errors)
    {
        var tokens = Tokenize(entry);
        if (tokens.Count == 0) return;

        int i = 0;
        string? constraintName = null;

        // Optional CONSTRAINT name
        if (tokens[i].Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
        {
            i++;
            if (i < tokens.Count && !IsConstraintTypeKeyword(tokens[i]))
                constraintName = UnquoteIdentifier(tokens[i++]);
        }

        if (i >= tokens.Count) return;
        var keyword = tokens[i++];

        // ── PRIMARY KEY ───────────────────────────────────────────────────────
        if (keyword.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) &&
            i < tokens.Count &&
            tokens[i].Equals("KEY", StringComparison.OrdinalIgnoreCase))
        {
            i++; // skip KEY
            // Skip optional CLUSTERED / NONCLUSTERED (SQL Server)
            if (i < tokens.Count &&
                (tokens[i].Equals("CLUSTERED",    StringComparison.OrdinalIgnoreCase) ||
                 tokens[i].Equals("NONCLUSTERED", StringComparison.OrdinalIgnoreCase)))
                i++;

            if (i < tokens.Count && tokens[i].StartsWith('('))
                pkCols.AddRange(ParseColumnList(tokens[i]));

            return;
        }

        // ── FOREIGN KEY ───────────────────────────────────────────────────────
        if (keyword.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase) &&
            i < tokens.Count &&
            tokens[i].Equals("KEY", StringComparison.OrdinalIgnoreCase))
        {
            i++; // skip KEY

            // Skip optional index name before source columns (MySQL)
            if (i < tokens.Count &&
                !tokens[i].StartsWith('(') &&
                !tokens[i].Equals("REFERENCES", StringComparison.OrdinalIgnoreCase))
                i++;

            if (i >= tokens.Count || !tokens[i].StartsWith('(')) return;
            var sourceCols = ParseColumnList(tokens[i++]);

            if (i >= tokens.Count ||
                !tokens[i].Equals("REFERENCES", StringComparison.OrdinalIgnoreCase))
                return;
            i++; // skip REFERENCES

            if (i >= tokens.Count) return;
            var targetTable = ReadQualifiedTableRef(tokens, ref i);

            var targetCols = new List<string>();
            if (i < tokens.Count && tokens[i].StartsWith('('))
                targetCols = ParseColumnList(tokens[i++]);

            bool cascade = i + 2 < tokens.Count &&
                           tokens[i].Equals("ON",      StringComparison.OrdinalIgnoreCase) &&
                           tokens[i + 1].Equals("DELETE",  StringComparison.OrdinalIgnoreCase) &&
                           tokens[i + 2].Equals("CASCADE", StringComparison.OrdinalIgnoreCase);

            foreach (var srcCol in sourceCols)
            {
                fks.Add(new ForeignKey
                {
                    SourceField   = srcCol,
                    TargetTable   = targetTable,
                    TargetField   = targetCols.Count > 0 ? targetCols[0] : "id",
                    CascadeDelete = cascade,
                    Kind          = ForeignKeyKind.Physical,
                });
            }
            return;
        }

        // ── UNIQUE ────────────────────────────────────────────────────────────
        if (keyword.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            // Skip optional INDEX / KEY keyword (MySQL)
            if (i < tokens.Count &&
                (tokens[i].Equals("INDEX", StringComparison.OrdinalIgnoreCase) ||
                 tokens[i].Equals("KEY",   StringComparison.OrdinalIgnoreCase)))
                i++;

            // Skip optional constraint name before column list
            if (i < tokens.Count && !tokens[i].StartsWith('('))
            {
                constraintName ??= UnquoteIdentifier(tokens[i]);
                i++;
            }

            if (i < tokens.Count && tokens[i].StartsWith('('))
            {
                var cols = ParseColumnList(tokens[i]);
                uniqueCs.Add(new UniqueConstraint
                {
                    Name    = constraintName ?? $"uq_{tableName}_{++seq}",
                    Columns = cols.AsReadOnly(),
                });
            }
            return;
        }

        // ── CHECK ─────────────────────────────────────────────────────────────
        if (keyword.Equals("CHECK", StringComparison.OrdinalIgnoreCase))
        {
            if (i < tokens.Count && tokens[i].StartsWith('('))
            {
                var expr = tokens[i][1..^1].Trim();
                checkCs.Add(new CheckConstraint
                {
                    Name       = constraintName ?? $"ck_{tableName}_{++seq}",
                    Expression = expr,
                });
            }
            return;
        }

        // KEY / INDEX (MySQL inline index) — skip in v1; index data comes from live introspection
    }

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits a SQL fragment into flat tokens: words/numbers, quoted identifiers,
    /// string literals, and parenthesised groups (each group is a single token
    /// including its outer parens).
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i      = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Backtick-quoted identifier  `name`
            if (c == '`')
            {
                int start = i++;
                while (i < text.Length && text[i] != '`') i++;
                if (i < text.Length) i++;
                tokens.Add(text[start..i]);
                continue;
            }

            // Double-quote-quoted identifier  "name"
            if (c == '"')
            {
                int start = i++;
                while (i < text.Length && text[i] != '"') i++;
                if (i < text.Length) i++;
                tokens.Add(text[start..i]);
                continue;
            }

            // Bracket-quoted identifier  [name]  (SQL Server / SQLite)
            if (c == '[')
            {
                int start = i++;
                while (i < text.Length && text[i] != ']') i++;
                if (i < text.Length) i++;
                tokens.Add(text[start..i]);
                continue;
            }

            // SQL Server N'...' unicode string literal
            if ((c == 'N' || c == 'n') &&
                i + 1 < text.Length && text[i + 1] == '\'')
            {
                var sb = new StringBuilder();
                sb.Append(text[i++]); // N
                sb.Append(text[i++]); // opening '
                while (i < text.Length)
                {
                    if (text[i] == '\'' &&
                        i + 1 < text.Length && text[i + 1] == '\'')
                    { sb.Append("''"); i += 2; }
                    else if (text[i] == '\'')
                    { sb.Append('\''); i++; break; }
                    else
                    { sb.Append(text[i++]); }
                }
                tokens.Add(sb.ToString());
                continue;
            }

            // Single-quoted string literal  '...'
            if (c == '\'')
            {
                var sb = new StringBuilder();
                sb.Append(text[i++]);
                while (i < text.Length)
                {
                    if (text[i] == '\'' &&
                        i + 1 < text.Length && text[i + 1] == '\'')
                    { sb.Append("''"); i += 2; }
                    else if (text[i] == '\'')
                    { sb.Append('\''); i++; break; }
                    else
                    { sb.Append(text[i++]); }
                }
                tokens.Add(sb.ToString());
                continue;
            }

            // Parenthesised group — entire group is one token including outer parens
            if (c == '(')
            {
                int start  = i;
                int depth  = 0;
                bool inStr = false;
                while (i < text.Length)
                {
                    char ch = text[i];
                    if (inStr)
                    {
                        if (ch == '\'' &&
                            i + 1 < text.Length && text[i + 1] == '\'')
                        { i += 2; continue; }
                        if (ch == '\'') inStr = false;
                        i++; continue;
                    }
                    if (ch == '\'') { inStr = true; i++; continue; }
                    if (ch == '(')  { depth++; i++; continue; }
                    if (ch == ')')  { depth--; i++; if (depth == 0) break; continue; }
                    i++;
                }
                tokens.Add(text[start..i]);
                continue;
            }

            // Word / keyword / number — also captures AUTO_INCREMENT with underscore
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                int start = i;
                while (i < text.Length &&
                       (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(text[start..i]);
                continue;
            }

            // Skip other punctuation (commas at depth 0 are handled before tokenising)
            i++;
        }

        return tokens;
    }

    // ── Default value extraction ──────────────────────────────────────────────

    private static string? ExtractDefaultValue(List<string> tokens, ref int i)
    {
        if (i >= tokens.Count) return null;

        var tok = tokens[i];

        // NULL default → no default value stored
        if (tok.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            i++;
            return null;
        }

        // Parenthesised expression: DEFAULT (expr)  or  DEFAULT ((value))
        if (tok.StartsWith('('))
        {
            i++;
            return tok[1..^1].Trim();
        }

        // Single-quoted or N'...' string literal
        if (tok.StartsWith('\'') ||
            (tok.Length >= 2 &&
             (tok[0] == 'N' || tok[0] == 'n') && tok[1] == '\''))
        {
            i++;
            return UnquoteString(tok);
        }

        // Any other value (number, keyword like CURRENT_TIMESTAMP, function call)
        // but not a column-modifier keyword
        if (!IsKnownModifier(tok))
        {
            i++;
            return tok;
        }

        return null;
    }

    // ── Type normalisation ────────────────────────────────────────────────────

    private static string NormalizeType(string rawType, DbProvider provider) =>
        provider switch
        {
            DbProvider.MySql     => NormalizeMySql(rawType.Trim()),
            DbProvider.Postgres  => NormalizePostgres(rawType.Trim()),
            DbProvider.Sqlite    => NormalizeSqlite(rawType.Trim()),
            DbProvider.SqlServer => NormalizeSqlServer(rawType.Trim()),
            _                    => rawType.Trim(),
        };

    private static readonly Regex s_intDisplayWidth = new(
        @"\b(TINYINT|SMALLINT|MEDIUMINT|INT|INTEGER|BIGINT)\((\d+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string NormalizeMySql(string raw)
    {
        // Preserve tinyint(1) — it is the MySQL boolean convention
        var upper = raw.ToUpperInvariant();
        if (upper == "TINYINT(1)") return "tinyint(1)";

        // Strip display widths from integer types: int(11) → int, etc.
        var result = s_intDisplayWidth.Replace(raw, m =>
        {
            var baseType = m.Groups[1].Value.ToLowerInvariant();
            return baseType == "integer" ? "int" : baseType;
        });

        // INTEGER → int (when no width)
        if (result.Equals("INTEGER", StringComparison.OrdinalIgnoreCase))
            return "int";

        return result.ToLowerInvariant();
    }

    private static string NormalizePostgres(string raw)
    {
        var lower = raw.ToLowerInvariant();
        return lower switch
        {
            "serial"      => "integer",
            "bigserial"   => "bigint",
            "smallserial" => "smallint",
            _             => lower,
        };
    }

    private static string NormalizeSqlite(string raw) =>
        raw.ToLowerInvariant() switch
        {
            "integer" => "integer",
            "text"    => "text",
            "real"    => "real",
            "blob"    => "blob",
            "numeric" => "numeric",
            var other => other,
        };

    // Matches precision/scale groups, e.g. "(18, 0)" or "( 10 , 2 )" — strips inner spaces.
    private static readonly Regex s_precisionSpacingRx = new(
        @"\(\s*(\d+)\s*,\s*(\d+)\s*\)",
        RegexOptions.Compiled);

    private static string NormalizeSqlServer(string raw)
    {
        var lower = raw.Trim().ToLowerInvariant();

        // dec(...) is an alias for decimal(...) in T-SQL
        if (lower.StartsWith("dec(", StringComparison.Ordinal))
            lower = "decimal" + lower[3..];

        // Remove spaces inside precision/scale parentheses: decimal(18, 0) → decimal(18,0)
        lower = s_precisionSpacingRx.Replace(lower, "($1,$2)");

        return lower;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string? schema, string name) ParseQualifiedName(string raw)
    {
        raw = raw.Trim();
        var identifiers = new List<string>();
        int i = 0;

        while (i < raw.Length)
        {
            if (char.IsWhiteSpace(raw[i]) || raw[i] == '.') { i++; continue; }

            // Quoted identifier: `…`, "…", […]
            if (raw[i] is '`' or '"' or '[')
            {
                char close = raw[i] == '[' ? ']' : raw[i];
                i++;
                int start = i;
                while (i < raw.Length && raw[i] != close) i++;
                identifiers.Add(raw[start..i]);
                if (i < raw.Length) i++;
                continue;
            }

            // Unquoted word
            {
                int start = i;
                while (i < raw.Length &&
                       (char.IsLetterOrDigit(raw[i]) || raw[i] == '_'))
                    i++;
                if (i > start) identifiers.Add(raw[start..i]);
                else           i++; // skip unknown char
            }
        }

        return identifiers.Count switch
        {
            0 => (null, string.Empty),
            1 => (null, identifiers[0]),
            _ => (identifiers[^2], identifiers[^1]),
        };
    }

    private static string UnquoteIdentifier(string token)
    {
        var t = token.Trim();
        if (t.Length < 2) return t;
        return (t[0], t[^1]) switch
        {
            ('`', '`') => t[1..^1],
            ('"', '"') => t[1..^1],
            ('[', ']') => t[1..^1],
            _          => t,
        };
    }

    private static string? UnquoteString(string token)
    {
        var t = token.Trim();
        // N'...' unicode literal (SQL Server)
        if (t.Length >= 3 &&
            t[0] is 'N' or 'n' && t[1] == '\'' && t[^1] == '\'')
            return t[2..^1].Replace("''", "'");
        // '...'
        if (t.Length >= 2 && t[0] == '\'' && t[^1] == '\'')
            return t[1..^1].Replace("''", "'");
        return t;
    }

    private static List<string> ParseColumnList(string parenToken)
    {
        var inner = parenToken.StartsWith('(') && parenToken.EndsWith(')')
            ? parenToken[1..^1]
            : parenToken;

        return inner.Split(',')
            .Select(c =>
            {
                // Strip ASC/DESC suffixes from index column lists
                var col = c.Trim();
                var tok = Tokenize(col);
                return tok.Count > 0 ? UnquoteIdentifier(tok[0]) : string.Empty;
            })
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();
    }

    /// <summary>
    /// Returns <c>true</c> when the token is a well-known column modifier keyword.
    /// Used to guard default-value extraction from accidentally consuming a modifier.
    /// </summary>
    private static bool IsKnownModifier(string t) =>
        t.Equals("NOT",           StringComparison.OrdinalIgnoreCase) ||
        t.Equals("NULL",          StringComparison.OrdinalIgnoreCase) ||
        t.Equals("DEFAULT",       StringComparison.OrdinalIgnoreCase) ||
        t.Equals("PRIMARY",       StringComparison.OrdinalIgnoreCase) ||
        t.Equals("UNIQUE",        StringComparison.OrdinalIgnoreCase) ||
        t.Equals("AUTO_INCREMENT",StringComparison.OrdinalIgnoreCase) ||
        t.Equals("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("IDENTITY",      StringComparison.OrdinalIgnoreCase) ||
        t.Equals("REFERENCES",    StringComparison.OrdinalIgnoreCase) ||
        t.Equals("COMMENT",       StringComparison.OrdinalIgnoreCase) ||
        t.Equals("COLLATE",       StringComparison.OrdinalIgnoreCase) ||
        t.Equals("CHARACTER",     StringComparison.OrdinalIgnoreCase) ||
        t.Equals("CHARSET",       StringComparison.OrdinalIgnoreCase) ||
        t.Equals("ON",            StringComparison.OrdinalIgnoreCase) ||
        t.Equals("CHECK",         StringComparison.OrdinalIgnoreCase) ||
        t.Equals("CONSTRAINT",    StringComparison.OrdinalIgnoreCase) ||
        t.Equals("GENERATED",     StringComparison.OrdinalIgnoreCase) ||
        t.Equals("VIRTUAL",       StringComparison.OrdinalIgnoreCase) ||
        t.Equals("STORED",        StringComparison.OrdinalIgnoreCase) ||
        t.Equals("PERSISTED",     StringComparison.OrdinalIgnoreCase) ||
        t.Equals("INVISIBLE",     StringComparison.OrdinalIgnoreCase) ||
        t.Equals("VISIBLE",       StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads 1–2 tokens to form a (possibly schema-qualified) table reference from
    /// a REFERENCES clause.  The tokenizer strips dots, so <c>[dbo].[Tbl]</c> becomes
    /// two consecutive tokens; this method re-joins them with a dot.
    /// </summary>
    private static string ReadQualifiedTableRef(List<string> tokens, ref int i)
    {
        if (i >= tokens.Count) return string.Empty;
        var firstPart = UnquoteIdentifier(tokens[i++]);

        // If the next token also looks like an identifier (and is not a clause keyword
        // that follows a REFERENCES target), treat it as the table part of schema.table.
        if (i < tokens.Count &&
            !tokens[i].StartsWith('(') &&
            IsTableNameToken(tokens[i]) &&
            !IsReferencesFollowKeyword(tokens[i]))
        {
            return firstPart + "." + UnquoteIdentifier(tokens[i++]);
        }
        return firstPart;
    }

    private static bool IsTableNameToken(string token) =>
        token.Length > 0 &&
        (char.IsLetter(token[0]) || token[0] == '_' ||
         token[0] == '`' || token[0] == '"' || token[0] == '[');

    /// <summary>
    /// Keywords that legally follow a table-name token in any context where
    /// <see cref="ReadQualifiedTableRef"/> is used (REFERENCES clause, ALTER TABLE, etc.)
    /// and therefore must NOT be consumed as the second half of a schema.table pair.
    /// </summary>
    private static bool IsReferencesFollowKeyword(string token) =>
        // REFERENCES clause terminators
        token.Equals("ON",         StringComparison.OrdinalIgnoreCase) ||
        token.Equals("NOT",        StringComparison.OrdinalIgnoreCase) ||
        token.Equals("MATCH",      StringComparison.OrdinalIgnoreCase) ||
        token.Equals("DEFERRABLE", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("INITIALLY",  StringComparison.OrdinalIgnoreCase) ||
        // ALTER TABLE action keywords — must not be confused with a table-name component
        token.Equals("ADD",        StringComparison.OrdinalIgnoreCase) ||
        token.Equals("DROP",       StringComparison.OrdinalIgnoreCase) ||
        token.Equals("ALTER",      StringComparison.OrdinalIgnoreCase) ||
        token.Equals("WITH",       StringComparison.OrdinalIgnoreCase) ||
        token.Equals("SET",        StringComparison.OrdinalIgnoreCase) ||
        token.Equals("NOCHECK",    StringComparison.OrdinalIgnoreCase) ||
        token.Equals("ENABLE",     StringComparison.OrdinalIgnoreCase) ||
        token.Equals("DISABLE",    StringComparison.OrdinalIgnoreCase) ||
        // Batch separator (SQL Server)
        token.Equals("GO",         StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string s) =>
        s.Length > 80 ? s[..77] + "..." : s;
}
