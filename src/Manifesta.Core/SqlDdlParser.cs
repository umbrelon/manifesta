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
/// computed columns, inline index definitions, ALTER TABLE mutations, and
/// standalone CREATE INDEX statements (when <c>includeMigrations = true</c>).
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

        /// <summary>
        /// Column-level mutations extracted from <c>ALTER TABLE</c> statements: ADD COLUMN,
        /// ADD CONSTRAINT FOREIGN KEY, ALTER/MODIFY COLUMN, DROP COLUMN, RENAME COLUMN.
        /// Only populated when <c>includeMigrations = true</c> is passed to
        /// <see cref="SqlDdlParser.Parse"/>. The caller applies these to the parsed
        /// <see cref="Tables"/> list in document order.
        /// </summary>
        public IReadOnlyList<AlterTableMutation> Mutations { get; init; } = [];

        /// <summary>
        /// View definitions extracted from <c>CREATE VIEW</c> statements.
        /// Each entry has <see cref="TableDefinition.IsView"/> = <c>true</c> and columns
        /// parsed from the <c>SELECT</c> column list with <c>type = "unknown"</c>.
        /// Populated regardless of <c>includeMigrations</c>.
        /// </summary>
        public IReadOnlyList<TableDefinition> Views { get; init; } = [];
    }

    /// <summary>
    /// Primary-key information extracted from an <c>ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY</c>
    /// statement.  Used to enrich tables whose CREATE TABLE body omits the PK constraint.
    /// </summary>
    public sealed record AlterTablePkAddition(
        string TableName,
        IReadOnlyList<string> Columns);

    /// <summary>Kind of ALTER TABLE column mutation or CREATE INDEX statement.</summary>
    public enum AlterTableMutationKind
    {
        AddColumn,
        AddForeignKey,
        AlterColumn,
        DropColumn,
        RenameColumn,
        /// <summary>Standalone <c>CREATE [UNIQUE] INDEX … ON &lt;table&gt; (…)</c> statement.</summary>
        CreateIndex,
    }

    /// <summary>
    /// A single column-level mutation extracted from an <c>ALTER TABLE</c> statement.
    /// The caller applies mutations in document order to the in-memory table map.
    /// </summary>
    public sealed record AlterTableMutation(
        AlterTableMutationKind Kind,
        string TableName)
    {
        /// <summary>Column definition for AddColumn / AlterColumn.</summary>
        public FieldDefinition?  Column  { get; init; }
        /// <summary>Foreign key for AddForeignKey.</summary>
        public ForeignKey?       Fk      { get; init; }
        /// <summary>Column name for DropColumn / RenameColumn / AlterColumn.</summary>
        public string?           ColName { get; init; }
        /// <summary>New column name for RenameColumn.</summary>
        public string?           NewName { get; init; }
        /// <summary>FIRST / column-name to place the new column AFTER (MySQL only).</summary>
        public string?           After   { get; init; }
        /// <summary>True when the new column goes first (MySQL FIRST keyword).</summary>
        public bool              First   { get; init; }
        /// <summary>Index definition for CreateIndex.</summary>
        public IndexDefinition?  Index   { get; init; }
    }

    /// <summary>
    /// Parses one or more CREATE TABLE statements from <paramref name="sql"/>.
    /// </summary>
    /// <param name="sql">Full DDL text (script or dump file).</param>
    /// <param name="provider">Dialect for type normalisation and syntax quirks.</param>
    /// <param name="schemaPrefix">
    /// When set, unqualified table names are prefixed with this schema
    /// (e.g. <c>"dbo"</c> → <c>"dbo.Customer"</c>).
    /// </param>
    /// <param name="includeMigrations">
    /// When <c>true</c>, also extracts ALTER TABLE mutations (ADD COLUMN, ADD FOREIGN KEY,
    /// ALTER/MODIFY COLUMN, DROP COLUMN, RENAME COLUMN) and returns them in
    /// <see cref="ParseResult.Mutations"/>. The caller is responsible for applying them
    /// in document order to the parsed tables.
    /// </param>
    public ParseResult Parse(string sql, DbProvider provider, string? schemaPrefix = null,
        bool includeMigrations = false)
    {
        var tables      = new List<TableDefinition>();
        var errors      = new List<string>();
        var typeAliases = ExtractTypeAliases(sql, provider);

        foreach (var block in ExtractCreateTableBlocks(sql))
        {
            var (table, blockErrors) = ParseCreateTable(block, provider, schemaPrefix, typeAliases);
            if (table is not null)
                tables.Add(table);
            errors.AddRange(blockErrors);
        }

        var views = ExtractCreateViews(sql, schemaPrefix);

        var pkAdditions = includeMigrations ? ExtractAlterTablePks(sql, schemaPrefix) : [];
        List<AlterTableMutation> mutations = [];
        if (includeMigrations)
        {
            mutations = ExtractAlterTableMutations(sql, provider, schemaPrefix);
            mutations.AddRange(ExtractCreateIndexStatements(sql, schemaPrefix));
        }
        return new ParseResult(tables.AsReadOnly(), errors.AsReadOnly())
        {
            PkAdditions = pkAdditions.AsReadOnly(),
            Mutations   = mutations.AsReadOnly(),
            Views       = views.AsReadOnly(),
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

            // Skip optional SQL Server WITH [NO]CHECK clause that precedes ADD in ALTER TABLE.
            // e.g. ALTER TABLE [dbo].[T] WITH CHECK ADD CONSTRAINT ...
            //      ALTER TABLE [dbo].[T] WITH NOCHECK ADD CONSTRAINT ...
            SkipWithCheckClause(tokens, ref i);

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

    // ── Phase 0b: extract ALTER TABLE column mutations ────────────────────────

    /// <summary>
    /// Scans <paramref name="sql"/> for ALTER TABLE statements that mutate columns or add
    /// foreign keys, and returns one <see cref="AlterTableMutation"/> per operation.
    /// Handles ADD [COLUMN], ADD CONSTRAINT FK, ALTER COLUMN, MODIFY [COLUMN],
    /// DROP [COLUMN], and RENAME COLUMN … TO.
    /// </summary>
    private static List<AlterTableMutation> ExtractAlterTableMutations(
        string sql, DbProvider provider, string? schemaPrefix)
    {
        var result = new List<AlterTableMutation>();
        var clean  = RemoveComments(sql);
        var tokens = Tokenize(clean);
        int i = 0;

        while (i < tokens.Count)
        {
            // Match: ALTER TABLE <name>
            if (!tokens[i].Equals("ALTER", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            if (i + 1 >= tokens.Count || !tokens[i + 1].Equals("TABLE", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            i += 2;

            if (i >= tokens.Count) break;
            var tableName = ReadQualifiedTableRef(tokens, ref i);
            if (!tableName.Contains('.') && schemaPrefix is not null)
                tableName = $"{schemaPrefix}.{tableName}";

            if (i >= tokens.Count) continue;

            // Skip optional SQL Server WITH [NO]CHECK clause before ADD
            SkipWithCheckClause(tokens, ref i);

            // ── ADD ─────────────────────────────────────────────────────────────
            if (tokens[i].Equals("ADD", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i >= tokens.Count) continue;

                // Optional COLUMN keyword
                if (tokens[i].Equals("COLUMN", StringComparison.OrdinalIgnoreCase))
                    i++;

                if (i >= tokens.Count) continue;

                // ADD CONSTRAINT [name] … — loop to handle comma-separated multi-constraint form.
                // SQL Server commonly groups several FKs in one ALTER TABLE … ADD statement:
                //   ALTER TABLE t ADD
                //       CONSTRAINT fk1 FOREIGN KEY (a) REFERENCES p (a),
                //       CONSTRAINT fk2 FOREIGN KEY (b) REFERENCES q (b);
                // Commas are punctuation that the tokenizer discards, so consecutive CONSTRAINT
                // tokens appear back-to-back after each FK definition is consumed.
                while (i < tokens.Count && tokens[i].Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
                {
                    i++; // skip CONSTRAINT
                    if (i < tokens.Count && !IsConstraintTypeKeyword(tokens[i]))
                        i++; // skip constraint name

                    if (i >= tokens.Count) break;

                    // FOREIGN KEY — extract as AddForeignKey mutation
                    if (tokens[i].Equals("FOREIGN", StringComparison.OrdinalIgnoreCase) &&
                        i + 1 < tokens.Count &&
                        tokens[i + 1].Equals("KEY", StringComparison.OrdinalIgnoreCase))
                    {
                        i += 2;
                        if (i < tokens.Count && !tokens[i].StartsWith('(') &&
                            !tokens[i].Equals("REFERENCES", StringComparison.OrdinalIgnoreCase))
                            i++; // optional index name (MySQL)

                        if (i >= tokens.Count || !tokens[i].StartsWith('(')) break;
                        var srcCols = ParseColumnList(tokens[i++]);

                        if (i >= tokens.Count || !tokens[i].Equals("REFERENCES", StringComparison.OrdinalIgnoreCase)) break;
                        i++;

                        var targetTable = ReadQualifiedTableRef(tokens, ref i);
                        var targetCols  = new List<string>();
                        if (i < tokens.Count && tokens[i].StartsWith('('))
                            targetCols = ParseColumnList(tokens[i++]);

                        // Consume ON DELETE/UPDATE <action> referential clauses (e.g. ON DELETE CASCADE,
                        // ON UPDATE NO ACTION). Commas between multiple clauses are tokenizer-discarded.
                        // Must advance past them so the next CONSTRAINT token is reachable.
                        bool cascade = false;
                        while (i < tokens.Count &&
                               tokens[i].Equals("ON", StringComparison.OrdinalIgnoreCase) &&
                               i + 1 < tokens.Count &&
                               (tokens[i + 1].Equals("DELETE", StringComparison.OrdinalIgnoreCase) ||
                                tokens[i + 1].Equals("UPDATE", StringComparison.OrdinalIgnoreCase)))
                        {
                            bool isDelete = tokens[i + 1].Equals("DELETE", StringComparison.OrdinalIgnoreCase);
                            i += 2; // skip ON DELETE / ON UPDATE
                            if (i < tokens.Count)
                            {
                                if (isDelete && tokens[i].Equals("CASCADE", StringComparison.OrdinalIgnoreCase))
                                    cascade = true;
                                i++; // skip action keyword (CASCADE, NO ACTION, SET NULL, etc.)
                            }
                        }

                        // Composite FK: pair source columns with target columns by index (diagonal).
                        // e.g. FOREIGN KEY (A, B) REFERENCES T (X, Y) → A→X and B→Y.
                        for (int fkIdx = 0; fkIdx < srcCols.Count; fkIdx++)
                        {
                            var targetField = fkIdx < targetCols.Count ? targetCols[fkIdx]
                                           : targetCols.Count > 0     ? targetCols[0]
                                           : "id";
                            result.Add(new AlterTableMutation(AlterTableMutationKind.AddForeignKey, tableName)
                            {
                                Fk = new ForeignKey
                                {
                                    SourceField   = srcCols[fkIdx],
                                    TargetTable   = targetTable,
                                    TargetField   = targetField,
                                    CascadeDelete = cascade,
                                    Kind          = ForeignKeyKind.Physical,
                                },
                            });
                        }
                    }
                    // PRIMARY KEY — already handled by ExtractAlterTablePks; skip over it so the
                    // while loop can continue to any subsequent CONSTRAINT clauses in this ADD.
                    else if (tokens[i].Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) &&
                             i + 1 < tokens.Count &&
                             tokens[i + 1].Equals("KEY", StringComparison.OrdinalIgnoreCase))
                    {
                        // Skip PRIMARY KEY <CLUSTERED?> (cols) <WITH (...)> <ON filegroup>
                        i += 2;
                        if (i < tokens.Count && (
                            tokens[i].Equals("CLUSTERED",    StringComparison.OrdinalIgnoreCase) ||
                            tokens[i].Equals("NONCLUSTERED", StringComparison.OrdinalIgnoreCase)))
                            i++;
                        if (i < tokens.Count && tokens[i].StartsWith('('))
                            i++; // skip column list
                        // Skip trailing storage options until next CONSTRAINT / ; / GO
                        while (i < tokens.Count &&
                               !tokens[i].Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase) &&
                               !tokens[i].Equals(";",           StringComparison.Ordinal) &&
                               !tokens[i].Equals("GO",          StringComparison.OrdinalIgnoreCase) &&
                               !tokens[i].Equals("ALTER",       StringComparison.OrdinalIgnoreCase) &&
                               !tokens[i].Equals("CREATE",      StringComparison.OrdinalIgnoreCase))
                            i++;
                    }
                    else
                    {
                        break; // unknown constraint type — bail out of the inner loop
                    }
                }

                // If the while loop consumed one or more CONSTRAINT clauses, skip to next statement.
                // Otherwise fall through to handle bare ADD COLUMN / ADD FOREIGN KEY / ADD PRIMARY KEY.
                if (i >= tokens.Count ||
                    tokens[i].Equals(";",      StringComparison.Ordinal) ||
                    tokens[i].Equals("GO",     StringComparison.OrdinalIgnoreCase) ||
                    tokens[i].Equals("ALTER",  StringComparison.OrdinalIgnoreCase) ||
                    tokens[i].Equals("CREATE", StringComparison.OrdinalIgnoreCase))
                    continue;

                // FOREIGN KEY without CONSTRAINT prefix
                if (tokens[i].Equals("FOREIGN", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < tokens.Count &&
                    tokens[i + 1].Equals("KEY", StringComparison.OrdinalIgnoreCase))
                    continue; // handled above only when CONSTRAINT is present; silently skip bare form

                // PRIMARY KEY without CONSTRAINT prefix — handled by ExtractAlterTablePks
                if (tokens[i].Equals("PRIMARY", StringComparison.OrdinalIgnoreCase))
                    continue;

                // ADD [COLUMN] colname type …
                // Parse as a column entry using the shared column-entry parser.
                // Re-assemble a synthetic "colname type …" fragment from remaining tokens
                // up to the next statement boundary (semicolon or GO).
                var colTokens = new List<string>();
                while (i < tokens.Count &&
                       !tokens[i].Equals(";",   StringComparison.Ordinal) &&
                       !tokens[i].Equals("GO",  StringComparison.OrdinalIgnoreCase) &&
                       !tokens[i].Equals("ALTER", StringComparison.OrdinalIgnoreCase))
                    colTokens.Add(tokens[i++]);

                // Detect optional MySQL FIRST / AFTER positioning at the end
                bool first = false;
                string? after = null;
                if (colTokens.Count >= 1 &&
                    colTokens[^1].Equals("FIRST", StringComparison.OrdinalIgnoreCase))
                {
                    first = true;
                    colTokens.RemoveAt(colTokens.Count - 1);
                }
                else if (colTokens.Count >= 2 &&
                         colTokens[^2].Equals("AFTER", StringComparison.OrdinalIgnoreCase))
                {
                    after = UnquoteIdentifier(colTokens[^1]);
                    colTokens.RemoveAt(colTokens.Count - 1);
                    colTokens.RemoveAt(colTokens.Count - 1);
                }

                if (colTokens.Count == 0) continue;

                var entry   = string.Join(" ", colTokens);
                var pkCols  = new List<string>();
                var fks     = new List<ForeignKey>();
                var colDef  = ParseColumnEntry(entry, pkCols, fks, provider, []);
                if (colDef is null) continue;

                result.Add(new AlterTableMutation(AlterTableMutationKind.AddColumn, tableName)
                {
                    Column = colDef,
                    First  = first,
                    After  = after,
                });

                // Inline FKs declared within the column entry
                foreach (var fk in fks)
                    result.Add(new AlterTableMutation(AlterTableMutationKind.AddForeignKey, tableName) { Fk = fk });

                continue;
            }

            // ── ALTER COLUMN (SQL Server / PostgreSQL) ───────────────────────────
            if (tokens[i].Equals("ALTER", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < tokens.Count &&
                tokens[i + 1].Equals("COLUMN", StringComparison.OrdinalIgnoreCase))
            {
                i += 2;
                if (i >= tokens.Count) continue;

                var colName = UnquoteIdentifier(tokens[i++]);
                // Collect remainder as column definition (type + modifiers)
                var colTokens = new List<string> { colName };
                while (i < tokens.Count &&
                       !tokens[i].Equals(";",   StringComparison.Ordinal) &&
                       !tokens[i].Equals("GO",  StringComparison.OrdinalIgnoreCase) &&
                       !tokens[i].Equals("ALTER", StringComparison.OrdinalIgnoreCase))
                    colTokens.Add(tokens[i++]);

                var entry  = string.Join(" ", colTokens);
                var colDef = ParseColumnEntry(entry, [], [], provider, []);
                if (colDef is null) continue;

                result.Add(new AlterTableMutation(AlterTableMutationKind.AlterColumn, tableName)
                {
                    Column  = colDef,
                    ColName = colName,
                });
                continue;
            }

            // ── MODIFY [COLUMN] (MySQL) ──────────────────────────────────────────
            if (tokens[i].Equals("MODIFY", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i < tokens.Count && tokens[i].Equals("COLUMN", StringComparison.OrdinalIgnoreCase))
                    i++;
                if (i >= tokens.Count) continue;

                var colName = UnquoteIdentifier(tokens[i]);
                var colTokens = new List<string>();
                while (i < tokens.Count &&
                       !tokens[i].Equals(";",   StringComparison.Ordinal) &&
                       !tokens[i].Equals("GO",  StringComparison.OrdinalIgnoreCase) &&
                       !tokens[i].Equals("ALTER", StringComparison.OrdinalIgnoreCase))
                    colTokens.Add(tokens[i++]);

                var entry  = string.Join(" ", colTokens);
                var colDef = ParseColumnEntry(entry, [], [], provider, []);
                if (colDef is null) continue;

                result.Add(new AlterTableMutation(AlterTableMutationKind.AlterColumn, tableName)
                {
                    Column  = colDef,
                    ColName = colName,
                });
                continue;
            }

            // ── DROP [COLUMN] ────────────────────────────────────────────────────
            if (tokens[i].Equals("DROP", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i < tokens.Count && tokens[i].Equals("COLUMN", StringComparison.OrdinalIgnoreCase))
                    i++;
                if (i >= tokens.Count) continue;

                var colName = UnquoteIdentifier(tokens[i++]);
                result.Add(new AlterTableMutation(AlterTableMutationKind.DropColumn, tableName)
                {
                    ColName = colName,
                });
                continue;
            }

            // ── RENAME COLUMN … TO … (PostgreSQL, SQLite) ───────────────────────
            if (tokens[i].Equals("RENAME", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < tokens.Count &&
                tokens[i + 1].Equals("COLUMN", StringComparison.OrdinalIgnoreCase))
            {
                i += 2;
                if (i >= tokens.Count) continue;

                var oldName = UnquoteIdentifier(tokens[i++]);
                // Skip optional TO keyword
                if (i < tokens.Count && tokens[i].Equals("TO", StringComparison.OrdinalIgnoreCase))
                    i++;
                if (i >= tokens.Count) continue;

                var newName = UnquoteIdentifier(tokens[i++]);
                result.Add(new AlterTableMutation(AlterTableMutationKind.RenameColumn, tableName)
                {
                    ColName = oldName,
                    NewName = newName,
                });
                continue;
            }

            // Other ALTER TABLE clauses (ENABLE CONSTRAINT, SET, WITH NOCHECK, etc.) — skip
            i++;
        }

        return result;
    }

    // ── Phase 0c: extract CREATE INDEX statements ────────────────────────────

    /// <summary>
    /// Scans <paramref name="sql"/> for standalone
    /// <c>CREATE [UNIQUE] [CLUSTERED|NONCLUSTERED] INDEX … ON &lt;table&gt; (&lt;cols&gt;) …</c>
    /// statements and returns one <see cref="AlterTableMutation"/> (<c>Kind = CreateIndex</c>)
    /// per match. Supports MySQL, PostgreSQL, SQLite, and SQL Server syntax including
    /// <c>INCLUDE (…)</c>, <c>WHERE &lt;filter&gt;</c>, and <c>USING &lt;method&gt;</c>.
    /// </summary>
    private static List<AlterTableMutation> ExtractCreateIndexStatements(
        string sql, string? schemaPrefix)
    {
        var result = new List<AlterTableMutation>();
        var clean  = RemoveComments(sql);
        var tokens = Tokenize(clean);
        int i = 0;

        while (i < tokens.Count)
        {
            // Match: CREATE (with look-ahead to confirm it's CREATE INDEX)
            if (!tokens[i].Equals("CREATE", StringComparison.OrdinalIgnoreCase)) { i++; continue; }

            // Peek ahead — skip optional modifier keywords to reach INDEX or give up.
            int j = i + 1;
            bool isUnique    = false;
            bool isClustered = false;

            if (j < tokens.Count && tokens[j].Equals("UNIQUE", StringComparison.OrdinalIgnoreCase))
            { isUnique = true; j++; }

            // SQL Server: CREATE PRIMARY XML INDEX … or CREATE XML INDEX …
            // "PRIMARY" here means "primary XML index" (not PK), not UNIQUE.
            if (j < tokens.Count && tokens[j].Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) &&
                j + 1 < tokens.Count && tokens[j + 1].Equals("XML", StringComparison.OrdinalIgnoreCase))
            { j += 2; } // consume PRIMARY XML
            else if (j < tokens.Count && tokens[j].Equals("XML", StringComparison.OrdinalIgnoreCase) &&
                     j + 1 < tokens.Count && tokens[j + 1].Equals("INDEX", StringComparison.OrdinalIgnoreCase))
            { j++; } // consume XML (INDEX follows)

            if (j < tokens.Count && (
                tokens[j].Equals("CLUSTERED",    StringComparison.OrdinalIgnoreCase) ||
                tokens[j].Equals("NONCLUSTERED", StringComparison.OrdinalIgnoreCase) ||
                tokens[j].Equals("FULLTEXT",     StringComparison.OrdinalIgnoreCase) ||
                tokens[j].Equals("SPATIAL",      StringComparison.OrdinalIgnoreCase)))
            {
                isClustered = tokens[j].Equals("CLUSTERED", StringComparison.OrdinalIgnoreCase);
                j++;
            }

            if (j >= tokens.Count || !tokens[j].Equals("INDEX", StringComparison.OrdinalIgnoreCase))
            { i++; continue; }

            // Confirmed: CREATE [UNIQUE?] [CLUSTERED?] INDEX — advance past INDEX
            i = j + 1;

            // Optional: CONCURRENTLY (PostgreSQL)
            if (i < tokens.Count && tokens[i].Equals("CONCURRENTLY", StringComparison.OrdinalIgnoreCase))
                i++;

            // Optional: IF NOT EXISTS
            if (i + 2 < tokens.Count &&
                tokens[i    ].Equals("IF",     StringComparison.OrdinalIgnoreCase) &&
                tokens[i + 1].Equals("NOT",    StringComparison.OrdinalIgnoreCase) &&
                tokens[i + 2].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                i += 3;

            // Index name
            if (i >= tokens.Count) continue;
            var indexName = UnquoteIdentifier(tokens[i++]);
            if (string.IsNullOrWhiteSpace(indexName)) continue;

            // ON keyword
            if (i >= tokens.Count || !tokens[i].Equals("ON", StringComparison.OrdinalIgnoreCase)) continue;
            i++;

            // Optional: ONLY (PostgreSQL partial index)
            if (i < tokens.Count && tokens[i].Equals("ONLY", StringComparison.OrdinalIgnoreCase))
                i++;

            // Table name
            if (i >= tokens.Count) continue;
            var tableName = ReadQualifiedTableRef(tokens, ref i);
            if (string.IsNullOrWhiteSpace(tableName)) continue;
            if (!tableName.Contains('.') && schemaPrefix is not null)
                tableName = $"{schemaPrefix}.{tableName}";

            // Optional: USING <method> (PostgreSQL / MySQL) — already blocked in ReadQualifiedTableRef
            if (i < tokens.Count && tokens[i].Equals("USING", StringComparison.OrdinalIgnoreCase))
            {
                i++; // skip USING
                if (i < tokens.Count && !tokens[i].StartsWith('(')) i++; // skip method name
            }

            // Column list  (col1 [ASC|DESC], col2 …)
            if (i >= tokens.Count || !tokens[i].StartsWith('(')) continue;
            var cols = ParseColumnList(tokens[i++]);
            if (cols.Count == 0) continue;

            // Optional: USING XML INDEX primary_name FOR {VALUE|PATH|PROPERTY} — SQL Server secondary XML index
            // Skip entire clause so it doesn't pollute INCLUDE / WHERE parsing below.
            if (i < tokens.Count && tokens[i].Equals("USING",  StringComparison.OrdinalIgnoreCase) &&
                i + 1 < tokens.Count && tokens[i + 1].Equals("XML", StringComparison.OrdinalIgnoreCase))
            {
                while (i < tokens.Count &&
                       !tokens[i].Equals(";",      StringComparison.Ordinal) &&
                       !tokens[i].Equals("GO",     StringComparison.OrdinalIgnoreCase) &&
                       !tokens[i].Equals("WITH",   StringComparison.OrdinalIgnoreCase) &&
                       !tokens[i].Equals("CREATE", StringComparison.OrdinalIgnoreCase) &&
                       !tokens[i].Equals("ALTER",  StringComparison.OrdinalIgnoreCase))
                    i++;
            }

            // Optional: INCLUDE (non-key columns) — SQL Server
            string? includedColumns = null;
            if (i < tokens.Count && tokens[i].Equals("INCLUDE", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i < tokens.Count && tokens[i].StartsWith('('))
                {
                    var inclCols = ParseColumnList(tokens[i++]);
                    if (inclCols.Count > 0)
                        includedColumns = string.Join(", ", inclCols);
                }
            }

            // Optional: WHERE <filter> — SQL Server filtered index
            string? filterExpression = null;
            if (i < tokens.Count && tokens[i].Equals("WHERE", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                var filterSb = new StringBuilder();
                while (i < tokens.Count &&
                       !tokens[i].Equals(";",   StringComparison.Ordinal) &&
                       !tokens[i].Equals("GO",  StringComparison.OrdinalIgnoreCase) &&
                       !tokens[i].Equals("ON",  StringComparison.OrdinalIgnoreCase) &&
                       !tokens[i].Equals("WITH", StringComparison.OrdinalIgnoreCase) &&
                       !tokens[i].Equals("CREATE", StringComparison.OrdinalIgnoreCase) &&
                       !tokens[i].Equals("ALTER",  StringComparison.OrdinalIgnoreCase))
                    filterSb.Append(tokens[i++]).Append(' ');
                filterExpression = filterSb.ToString().TrimEnd();
            }

            result.Add(new AlterTableMutation(AlterTableMutationKind.CreateIndex, tableName)
            {
                Index = new IndexDefinition
                {
                    Name             = indexName,
                    Columns          = cols.AsReadOnly(),
                    IsUnique         = isUnique,
                    IsClustered      = isClustered,
                    IncludedColumns  = includedColumns,
                    FilterExpression = filterExpression,
                    IsFiltered       = filterExpression is not null,
                },
            });
        }

        return result;
    }

    // ── Phase 0b: extract user-defined type aliases (SQL Server CREATE TYPE … FROM …) ──

    /// <summary>
    /// Scans <paramref name="sql"/> for <c>CREATE TYPE [name] FROM base_type[(precision)]</c>
    /// statements (SQL Server only) and returns a case-insensitive map of alias → normalised
    /// base type. The map is empty for all other providers.
    /// </summary>
    private static Dictionary<string, string> ExtractTypeAliases(string sql, DbProvider provider)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (provider != DbProvider.SqlServer) return result;

        var clean  = RemoveComments(sql);
        var tokens = Tokenize(clean);
        int i      = 0;

        while (i < tokens.Count)
        {
            if (!tokens[i].Equals("CREATE", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            i++;
            if (i >= tokens.Count || !tokens[i].Equals("TYPE", StringComparison.OrdinalIgnoreCase)) continue;
            i++;

            // Type name — possibly schema-qualified (tokenizer skips '.', so [dbo].[Name]
            // comes as two consecutive tokens); strip schema prefix to get the bare alias name.
            if (i >= tokens.Count) continue;
            var typeName = ReadQualifiedTableRef(tokens, ref i);
            var dot = typeName.LastIndexOf('.');
            if (dot >= 0) typeName = typeName[(dot + 1)..];

            // FROM keyword
            if (i >= tokens.Count || !tokens[i].Equals("FROM", StringComparison.OrdinalIgnoreCase)) continue;
            i++;

            // Base type (possibly bracket-quoted) + optional precision
            if (i >= tokens.Count) continue;
            var baseType = UnquoteIdentifier(tokens[i++]);
            if (i < tokens.Count && tokens[i].StartsWith('('))
                baseType += tokens[i++];

            var normalized = NormalizeSqlServer(baseType);
            if (!string.IsNullOrWhiteSpace(typeName) && !string.IsNullOrWhiteSpace(normalized))
                result[typeName] = normalized;
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

            // PostgreSQL dollar-quoted string: $tag$...$tag$
            // Erase the body so DDL inside stored functions is invisible to the parser.
            // The tag is $<identifier>$ or $$ (empty tag).
            if (sql[i] == '$')
            {
                // Collect the tag: $ + optional word chars + $
                int tagStart = i;
                int j = i + 1;
                while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_'))
                    j++;
                if (j < sql.Length && sql[j] == '$')
                {
                    // Valid dollar-quote tag found
                    var tag = sql[tagStart..(j + 1)]; // e.g. "$_$" or "$$"
                    int bodyStart = j + 1;
                    int closeIdx = sql.IndexOf(tag, bodyStart, StringComparison.Ordinal);
                    if (closeIdx >= 0)
                    {
                        // Emit the opening tag, blank the body, emit the closing tag
                        sb.Append(tag);
                        sb.Append(' ', closeIdx - bodyStart);
                        sb.Append(tag);
                        i = closeIdx + tag.Length;
                        continue;
                    }
                    // Unterminated dollar-quote — fall through and emit the $ literally
                }
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
        string block, DbProvider provider, string? schemaPrefix,
        IReadOnlyDictionary<string, string>? typeAliases = null)
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

        // Apply SQL Server user-defined type alias substitution (e.g. [Name] → nvarchar(50))
        if (typeAliases is { Count: > 0 })
        {
            fields = fields
                .Select(f => typeAliases.TryGetValue(f.Type, out var resolved)
                    ? f with { Type = resolved }
                    : f)
                .ToList();
        }

        // Merge inline PK markers into pkCols if no table-level PK constraint was found
        if (pkCols.Count == 0)
            pkCols.AddRange(fields.Where(f => f.IsPrimaryKey).Select(f => f.Name));

        // PK columns are always NOT NULL and must be marked IsPrimaryKey — enforce this
        // regardless of whether the DDL writer omitted NOT NULL (e.g. IDENTITY columns)
        // or the PK was declared via a table-level CONSTRAINT rather than inline.
        var pkSet = new HashSet<string>(pkCols, StringComparer.OrdinalIgnoreCase);
        fields = fields
            .Select(f => pkSet.Contains(f.Name)
                ? f with { Nullable = false, IsPrimaryKey = true }
                : f)
            .ToList();

        // Qualify unqualified FK target tables with the schema prefix so that
        // validate cross can resolve them by their full name (e.g. "artist" → "public.artist").
        // Applies to both inline REFERENCES on column entries and table-level FOREIGN KEY constraints.
        if (schemaPrefix is not null)
        {
            for (int fi = 0; fi < fks.Count; fi++)
            {
                if (!fks[fi].TargetTable.Contains('.'))
                    fks[fi] = fks[fi] with { TargetTable = $"{schemaPrefix}.{fks[fi].TargetTable}" };
            }
        }

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

        if (idx >= tokens.Count) return false;

        // MySQL SPATIAL KEY / SPATIAL INDEX and FULLTEXT KEY / FULLTEXT INDEX
        // are index-type qualifiers, not column definitions.  Treat them as
        // constraint entries so they are routed to ParseConstraintEntry and
        // silently skipped (like any other inline KEY/INDEX in v1), rather than
        // being misread as a column named "SPATIAL" or "FULLTEXT".
        // NOTE: FULLTEXT is also a valid PostgreSQL column *name* (e.g. Pagila's
        // `fulltext tsvector` column), so only treat it as a constraint marker when
        // it is immediately followed by KEY or INDEX.
        // SQL Server PERIOD FOR SYSTEM_TIME (col1, col2) appears inside temporal
        // table CREATE TABLE bodies and must be similarly skipped — otherwise the
        // parser creates a phantom column named "PERIOD".
        if (tokens[idx].Equals("SPATIAL", StringComparison.OrdinalIgnoreCase) ||
            tokens[idx].Equals("PERIOD",  StringComparison.OrdinalIgnoreCase))
            return true;

        if (tokens[idx].Equals("FULLTEXT", StringComparison.OrdinalIgnoreCase) &&
            idx + 1 < tokens.Count &&
            (tokens[idx + 1].Equals("KEY",   StringComparison.OrdinalIgnoreCase) ||
             tokens[idx + 1].Equals("INDEX", StringComparison.OrdinalIgnoreCase)))
            return true;

        return IsConstraintTypeKeyword(tokens[idx]);
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
        // Also handles method-call form: name AS [Col].[Method]()
        if (tokens[i].Equals("AS", StringComparison.OrdinalIgnoreCase))
        {
            i++;

            // Collect all tokens that form the expression, stopping at known column
            // modifiers (PERSISTED, NOT, NULL, CONSTRAINT, WITH, ON, …).
            // The tokenizer represents `[A].[B]()` as separate tokens: `[A]`, `[B]`, `()`.
            // The dot between identifiers is skipped as punctuation — so we just
            // concatenate consecutive identifier/paren tokens, rejoining with '.' when
            // both sides look like identifiers.
            string? expr = null;
            if (i < tokens.Count && !IsComputedExpressionTerminator(tokens[i]))
            {
                var exprSb = new StringBuilder();
                while (i < tokens.Count && !IsComputedExpressionTerminator(tokens[i]))
                {
                    var tok = tokens[i++];
                    // Parenthesised groups attach directly to preceding token
                    if (tok.StartsWith('('))
                        exprSb.Append(tok);
                    else if (exprSb.Length > 0 && !exprSb.ToString().EndsWith('('))
                        exprSb.Append('.').Append(tok);
                    else
                        exprSb.Append(tok);
                }
                var raw = exprSb.ToString().Trim().Trim('.');

                // If the entire expression is parenthesised, strip the outer parens
                // to match what sys.computed_columns stores (it adds its own wrapping).
                expr = raw.StartsWith('(') && raw.EndsWith(')')
                    ? raw[1..^1].Trim()
                    : raw;
            }

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

        // PostgreSQL array suffix — the tokenizer turns `[]` into a bracket-quoted
        // token immediately after the base type (e.g. integer[], text[], varchar[]).
        if (i < tokens.Count && tokens[i] == "[]")
            typeTokens.Add(tokens[i++]);

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
            // SQL Server system-time columns use: GENERATED ALWAYS AS ROW START/END HIDDEN
            // Stop before NOT/NULL so the modifiers loop can apply nullability correctly.
            i++;
            // Skip ALWAYS / BY DEFAULT / AS / ROW / START / END / HIDDEN etc.
            while (i < tokens.Count &&
                   !tokens[i].StartsWith('(') &&
                   !tokens[i].Equals("NOT",      StringComparison.OrdinalIgnoreCase) &&
                   !tokens[i].Equals("NULL",      StringComparison.OrdinalIgnoreCase) &&
                   !tokens[i].Equals("STORED",    StringComparison.OrdinalIgnoreCase) &&
                   !tokens[i].Equals("VIRTUAL",   StringComparison.OrdinalIgnoreCase) &&
                   !tokens[i].Equals("IDENTITY",  StringComparison.OrdinalIgnoreCase))
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

            // IDENTITY [(seed, increment)] — SQL Server; implies NOT NULL
            if (tok.Equals("IDENTITY", StringComparison.OrdinalIgnoreCase))
            {
                nullable = false;
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
            // HIDDEN — SQL Server system-time columns (GENERATED ALWAYS AS ROW START/END HIDDEN); strip
            if (tok.Equals("INVISIBLE", StringComparison.OrdinalIgnoreCase) ||
                tok.Equals("VISIBLE",   StringComparison.OrdinalIgnoreCase) ||
                tok.Equals("HIDDEN",    StringComparison.OrdinalIgnoreCase))
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

            // CONSTRAINT [name] <type> — two distinct cases:
            //   (a) Inline column constraint without a column list, e.g.
            //       [Id] int CONSTRAINT PK_T PRIMARY KEY
            //       → treat as PRIMARY KEY on the current column.
            //   (b) Table-level constraint appended to the last column due to a
            //       missing comma separator, e.g.
            //       [LastUpdated] datetime NOT NULL
            //       CONSTRAINT PK_T PRIMARY KEY CLUSTERED (Id) ON [PRIMARY]
            //       → register the listed columns as PK; do NOT mark current column.
            if (tok.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
            {
                i++; // skip CONSTRAINT
                // Skip optional constraint name
                if (i < tokens.Count && !IsConstraintTypeKeyword(tokens[i]))
                    i++;

                // Named DEFAULT constraint: CONSTRAINT DF_xxx DEFAULT (value)
                // Let the DEFAULT handler in the main loop pick up the value.
                if (i < tokens.Count && tokens[i].Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (i + 1 < tokens.Count &&
                    tokens[i].Equals("PRIMARY",   StringComparison.OrdinalIgnoreCase) &&
                    tokens[i + 1].Equals("KEY", StringComparison.OrdinalIgnoreCase))
                {
                    i += 2; // skip PRIMARY KEY
                    // Skip optional CLUSTERED / NONCLUSTERED
                    if (i < tokens.Count &&
                        (tokens[i].Equals("CLUSTERED",    StringComparison.OrdinalIgnoreCase) ||
                         tokens[i].Equals("NONCLUSTERED", StringComparison.OrdinalIgnoreCase)))
                        i++;

                    if (i < tokens.Count && tokens[i].StartsWith('('))
                    {
                        // Column list present → table-level constraint; register those columns
                        pkCols.AddRange(ParseColumnList(tokens[i]));
                        // Do NOT mark the current column as PK
                    }
                    else
                    {
                        // No column list → inline constraint on the current column
                        isPk     = true;
                        nullable = false;
                    }
                }

                // Remaining tokens (ON [filegroup], WITH (...), etc.) are storage options; stop
                break;
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
            Default            = NormalizeDefault(defaultVal, provider),
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
                // Normalise to SQL Server canonical form so the registry matches the live
                // introspector and pre-merge drift on CHECK constraints is eliminated.
                if (provider == DbProvider.SqlServer)
                    expr = NormalizeSqlServerCheckExpression(expr);
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

            // Word / keyword / number — also captures AUTO_INCREMENT with underscore.
            // When a word is immediately followed by '.' and another word (no whitespace),
            // the two are combined into a single schema-qualified token (e.g. public.year,
            // public.mpaa_rating) so the type-builder receives the full qualified name.
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                int start = i;
                while (i < text.Length &&
                       (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                // Optional single schema qualifier: word.word (no spaces around dot)
                if (i < text.Length && text[i] == '.' &&
                    i + 1 < text.Length &&
                    (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_' || text[i + 1] == '"'))
                {
                    i++; // consume the dot
                    while (i < text.Length &&
                           (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                        i++;
                }
                tokens.Add(text[start..i]);
                continue;
            }

            // Negative number literal: -N (e.g. DEFAULT -1, DEFAULT -100)
            // Only treat '-' as a unary minus when it immediately precedes digits so
            // that binary minus in expressions (which are always parenthesised in SQL
            // Server default contexts) is not accidentally captured here.
            if (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                int start = i++; // include the '-'
                while (i < text.Length && char.IsLetterOrDigit(text[i]))
                    i++;
                tokens.Add(text[start..i]);
                continue;
            }

            // Comparison / assignment operators: >=, <=, <>, !=, >, <, =
            // These appear in CHECK constraint expressions and are needed by the
            // canonical form normaliser.  They are safe to add at depth 0 because
            // in DDL structural positions any comparison expression is always inside
            // a parenthesised group (e.g. CHECK ([col] > 0)) and the whole group is
            // emitted as a single token — so the inner operator is never seen here.
            if (c is '>' or '<' or '=' or '!')
            {
                int start = i++;
                if (i < text.Length && text[i] == '=') i++;              // >=  <=  !=  ==
                else if (c == '<' && i < text.Length && text[i] == '>') i++; // <>
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
        // but not a column-modifier keyword.
        // The tokenizer splits "space(1)" into two tokens: "space" and "(1)".
        // If the next token is a parenthesised group, stitch them back into one
        // function-call string so normalisation and drift comparison work correctly.
        if (!IsKnownModifier(tok))
        {
            i++;
            if (i < tokens.Count && tokens[i].StartsWith('('))
                return tok + tokens[i++];
            return tok;
        }

        return null;
    }

    // ── Default-value normalisation ───────────────────────────────────────────

    private static string? NormalizeDefault(string? value, DbProvider provider)
    {
        if (value is null) return null;
        if (provider != DbProvider.SqlServer) return value;
        return NormalizeSqlServerDefault(value);
    }

    /// <summary>
    /// Canonicalises SQL Server default expressions that the DDL parser may see
    /// as bare keywords (no parentheses) to their standard function-call form,
    /// matching what <c>sys.default_constraints</c> returns when introspecting a
    /// live database.
    /// </summary>
    private static string NormalizeSqlServerDefault(string value)
    {
        // Already has parentheses — only lowercase the function name, don't add another pair.
        // Handles: getdate(), GETDATE(), suser_name(), SUSER_NAME(), newid(), etc.
        var lower = value.ToLowerInvariant();
        if (lower is "getdate()"
                  or "suser_name()"
                  or "suser_sname()"
                  or "newid()"
                  or "newsequentialid()"
                  or "getutcdate()"
                  or "sysutcdatetime()"
                  or "sysdatetime()")
            return lower;

        // Bare keyword / function name without parentheses — add them and lowercase.
        var result = lower switch
        {
            "getdate"          => "getdate()",
            "current_timestamp"=> "getdate()",   // T-SQL synonym
            "system_user"      => "suser_sname()", // T-SQL niladic synonym for suser_sname()
            "suser_name"       => "suser_name()",
            "suser_sname"      => "suser_sname()",
            "newid"            => "newid()",
            "newsequentialid"  => "newsequentialid()",
            "getutcdate"       => "getutcdate()",
            "sysutcdatetime"   => "sysutcdatetime()",
            "sysdatetime"      => "sysdatetime()",
            _ => null,
        };
        if (result is not null) return result;

        // space(N) → space((N)): SQL Server stores integer literal arguments
        // wrapped in an extra pair of parens in sys.default_constraints.
        var spaceMatch = s_spaceArgRx.Match(value);
        if (spaceMatch.Success)
            return $"space(({spaceMatch.Groups[1].Value}))";

        // Unseparated date/datetime literals must be quoted to match the form
        // sys.default_constraints returns after stripping its outer parentheses.
        // Handles both:
        //   DEFAULT 20000101           (bare)   → '20000101'
        //   DEFAULT '20000101'         (quoted) → '20000101'  (UnquoteString stripped the quotes)
        //   DEFAULT '29991231 23:59:59'          → '29991231 23:59:59'
        if (s_sqlServerDateLiteralRx.IsMatch(value))
            return $"'{value}'";

        return value;
    }

    // ── CHECK expression normalisation (SQL Server) ───────────────────────────

    /// <summary>
    /// Converts a SQL Server CHECK constraint expression to the canonical internal form
    /// stored in <c>sys.check_constraints</c> — the same form the live introspector returns.
    /// Applying this at DDL-parse time eliminates pre-merge drift on CHECK constraints.
    ///
    /// Rules:
    ///   • <c>BETWEEN a AND b</c> → <c>&gt;=(a) AND &lt;=(b)</c>
    ///   • <c>IN (v1, v2)</c>     → <c>=v2 OR =v1</c> (SQL Server reverses the list)
    ///   • Numeric literals       → wrapped in parentheses everywhere
    ///   • Function names         → lowercased
    ///   • Spaces around operators → removed
    ///   • Sub-expression parens in OR/AND chains → stripped
    ///   • Whole expression       → wrapped in <c>(…)</c>
    /// </summary>
    public static string NormalizeSqlServerCheckExpression(string rawExpr)
    {
        if (string.IsNullOrWhiteSpace(rawExpr)) return rawExpr;
        var tokens = Tokenize(rawExpr.Trim());
        if (tokens.Count == 0) return rawExpr;
        return "(" + EmitCheck(tokens).Trim() + ")";
    }

    /// <summary>Emits the canonical body of a check expression (without the outer parens).</summary>
    private static string EmitCheck(List<string> tokens)
    {
        // ── BETWEEN a AND b → lhs>=(a) AND lhs<=(b) ──────────────────────────
        int betweenIdx = FindCheckKeyword(tokens, "BETWEEN");
        if (betweenIdx > 0)
        {
            int andIdx = FindCheckKeyword(tokens, "AND", betweenIdx + 1);
            if (andIdx > betweenIdx + 1 && andIdx + 1 < tokens.Count)
            {
                var lhs  = EmitCheckTokens(tokens, 0, betweenIdx);
                var low  = EmitCheckBound(tokens, betweenIdx + 1, andIdx);
                var high = EmitCheckBound(tokens, andIdx + 1, tokens.Count);
                return $"{lhs}>={low} AND {lhs}<={high}";
            }
        }

        // ── expr IN (v1, v2, …) → expr=vN OR … OR expr=v1 (reversed) ─────────
        int inIdx = FindCheckKeyword(tokens, "IN");
        if (inIdx > 0 && inIdx + 1 < tokens.Count && tokens[inIdx + 1].StartsWith('('))
        {
            var lhs    = EmitCheckTokens(tokens, 0, inIdx);
            var values = SplitCheckCommas(tokens[inIdx + 1][1..^1]);
            values.Reverse();
            return string.Join(" OR ", values.Select(v => $"{lhs}={v.Trim()}"));
        }

        // ── token-by-token emission ───────────────────────────────────────────
        return EmitCheckTokens(tokens, 0, tokens.Count);
    }

    private static string EmitCheckTokens(List<string> tokens, int start, int end)
    {
        var sb = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            var tok = tokens[i];

            // Parenthesised group
            if (tok.StartsWith('(') && tok.EndsWith(')'))
            {
                bool isFunctionArgs = i > start && IsCheckUnquotedIdentifier(tokens[i - 1]);
                if (isFunctionArgs)
                {
                    sb.Append('(');
                    sb.Append(EmitCheckFunctionArgs(tok[1..^1]));
                    sb.Append(')');
                }
                else
                {
                    // Sub-expression in OR/AND chain — strip its outer parens
                    sb.Append(EmitCheck(Tokenize(tok[1..^1])).Trim());
                }
                continue;
            }

            // Logical connectors and predicates — uppercase with surrounding spaces
            if (IsCheckLogicalKeyword(tok))
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                sb.Append(tok.ToUpperInvariant());
                sb.Append(' ');
                continue;
            }

            // Comparison operators — no spaces; wrap next token if it is a numeric literal
            if (IsCheckComparisonOp(tok))
            {
                sb.Append(tok);
                if (i + 1 < end && IsCheckNumericLiteral(tokens[i + 1]))
                    sb.Append('(').Append(tokens[++i]).Append(')');
                continue;
            }

            // Unquoted identifiers (function names, SQL dateparts, etc.) — lowercase
            if (IsCheckUnquotedIdentifier(tok))
            {
                sb.Append(tok.ToLowerInvariant());
                continue;
            }

            // Bracket-quoted identifiers, string literals, everything else — as-is
            sb.Append(tok);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Emits a single BETWEEN bound value.
    /// Standalone numeric tokens are wrapped in parentheses; all other values pass through
    /// <see cref="EmitCheckTokens"/> (which handles function calls, string literals, etc.).
    /// </summary>
    private static string EmitCheckBound(List<string> tokens, int start, int end)
    {
        if (end - start == 1 && IsCheckNumericLiteral(tokens[start]))
            return $"({tokens[start]})";
        return EmitCheckTokens(tokens, start, end);
    }

    /// <summary>
    /// Processes the contents of a function call's argument list.
    /// Splits on top-level commas, normalises each argument, and joins with ",".
    /// </summary>
    private static string EmitCheckFunctionArgs(string argsContent)
    {
        if (string.IsNullOrEmpty(argsContent)) return string.Empty;
        var parts = SplitCheckCommas(argsContent);
        return string.Join(",", parts.Select(p => EmitCheckArg(p.Trim())));
    }

    private static string EmitCheckArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return string.Empty;
        var tokens = Tokenize(arg);
        if (tokens.Count == 0) return arg;

        // Single numeric literal — wrap
        if (tokens.Count == 1 && IsCheckNumericLiteral(tokens[0]))
            return $"({tokens[0]})";

        // Single unquoted identifier — lowercase
        if (tokens.Count == 1 && IsCheckUnquotedIdentifier(tokens[0]))
            return tokens[0].ToLowerInvariant();

        // Function call: identifier + paren group
        if (tokens.Count == 2 &&
            IsCheckUnquotedIdentifier(tokens[0]) &&
            tokens[1].StartsWith('(') && tokens[1].EndsWith(')'))
            return $"{tokens[0].ToLowerInvariant()}({EmitCheckFunctionArgs(tokens[1][1..^1])})";

        // Default
        return EmitCheckTokens(tokens, 0, tokens.Count);
    }

    /// <summary>
    /// Splits a string on commas that are at paren/bracket depth 0, correctly handling
    /// single-quoted string literals (including SQL Server N'…' Unicode literals and
    /// escaped quotes '' within strings).
    /// </summary>
    private static List<string> SplitCheckCommas(string text)
    {
        var parts = new List<string>();
        int depth = 0;
        bool inStr = false;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inStr)
            {
                if (c == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }
                if (c == '\'') inStr = false;
                continue;
            }
            // N'...' Unicode string literal (SQL Server)
            if ((c == 'N' || c == 'n') && i + 1 < text.Length && text[i + 1] == '\'')
                { i++; inStr = true; continue; }
            if (c == '\'') { inStr = true; continue; }
            if (c == '(' || c == '[') { depth++; continue; }
            if (c == ')' || c == ']') { depth--; continue; }
            if (c == ',' && depth == 0) { parts.Add(text[start..i]); start = i + 1; }
        }
        parts.Add(text[start..]);
        return parts;
    }

    private static int FindCheckKeyword(List<string> tokens, string keyword, int startIdx = 0) =>
        tokens.FindIndex(startIdx, t => t.Equals(keyword, StringComparison.OrdinalIgnoreCase));

    private static bool IsCheckLogicalKeyword(string tok) =>
        tok.Equals("AND",  StringComparison.OrdinalIgnoreCase) ||
        tok.Equals("OR",   StringComparison.OrdinalIgnoreCase) ||
        tok.Equals("IS",   StringComparison.OrdinalIgnoreCase) ||
        tok.Equals("NOT",  StringComparison.OrdinalIgnoreCase) ||
        tok.Equals("NULL", StringComparison.OrdinalIgnoreCase);

    private static bool IsCheckComparisonOp(string tok) =>
        tok is ">" or ">=" or "<" or "<=" or "=" or "<>";

    private static bool IsCheckNumericLiteral(string tok) =>
        tok.Length > 0 &&
        (char.IsDigit(tok[0]) || (tok[0] == '-' && tok.Length > 1 && char.IsDigit(tok[1])));

    private static bool IsCheckUnquotedIdentifier(string tok) =>
        tok.Length > 0 &&
        (char.IsLetter(tok[0]) || tok[0] == '_') &&
        !IsCheckLogicalKeyword(tok) &&
        !tok.Equals("BETWEEN", StringComparison.OrdinalIgnoreCase) &&
        !tok.Equals("IN",      StringComparison.OrdinalIgnoreCase);

    // ── CREATE VIEW parsing ───────────────────────────────────────────────────

    private static readonly Regex s_createViewRx = new(
        @"CREATE\s+(?:OR\s+REPLACE\s+)?(?:TEMP(?:ORARY)?\s+)?VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_viewAsAliasRx = new(
        @"\bAS\s+(?:\[([^\]]+)\]|""([^""]+)""|`([^`]+)`|(\w+))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // SQL Server assignment-style alias: [alias] = expr  or  alias = expr
    private static readonly Regex s_viewAssignAliasRx = new(
        @"^(?:\[([^\]]+)\]|(\w+))\s*=\s*\S",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_viewDotColRx = new(
        @"\.(?:\[([^\]]+)\]|""([^""]+)""|`([^`]+)`|(\w+))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_viewBareColRx = new(
        @"^(?:\[([^\]]+)\]|""([^""]+)""|`([^`]+)`|(\w+))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Scans <paramref name="sql"/> for <c>CREATE [OR REPLACE] VIEW</c> statements,
    /// parses the view name and SELECT column list, and returns one
    /// <see cref="TableDefinition"/> per view with <see cref="TableDefinition.IsView"/> = <c>true</c>.
    /// Column types are stored as <c>"unknown"</c> — <c>db merge</c> will populate real types
    /// from the live database.
    /// </summary>
    private static List<TableDefinition> ExtractCreateViews(string sql, string? schemaPrefix)
    {
        var result = new List<TableDefinition>();
        var clean  = RemoveComments(sql);
        int searchFrom = 0;

        while (true)
        {
            var m = s_createViewRx.Match(clean, searchFrom);
            if (!m.Success) break;

            int nameStart = m.Index + m.Length;

            // Read view name (possibly schema-qualified) using the token stream
            var nameTokens = Tokenize(clean[nameStart..Math.Min(nameStart + 120, clean.Length)]);
            if (nameTokens.Count == 0) { searchFrom = nameStart; continue; }

            int nameConsumed = 0;
            var viewName = ReadQualifiedTableRef(nameTokens, ref nameConsumed);
            if (string.IsNullOrWhiteSpace(viewName)) { searchFrom = nameStart + 1; continue; }

            if (!viewName.Contains('.') && schemaPrefix is not null)
                viewName = $"{schemaPrefix}.{viewName}";

            // Find the body: look for AS then SELECT in the remaining text
            // Scan ahead (up to ~16 KB) for AS … SELECT … FROM
            int scanEnd = Math.Min(nameStart + 16_384, clean.Length);
            string segment = clean[nameStart..scanEnd];

            // Find SELECT keyword position
            var segTokens = Tokenize(segment);
            int selectPos = segTokens.FindIndex(t => t.Equals("SELECT", StringComparison.OrdinalIgnoreCase));
            if (selectPos < 0) { searchFrom = nameStart + 1; continue; }

            // Find the first FROM keyword after SELECT.
            // The tokenizer produces complete (…) groups as single tokens, so any FROM
            // inside a subquery would be inside a paren group token — not a standalone
            // FROM token.  A plain linear scan is therefore correct and sufficient.
            int fromPos = segTokens.FindIndex(selectPos + 1,
                t => t.Equals("FROM", StringComparison.OrdinalIgnoreCase));

            // Build the SELECT column list text
            // Re-extract the raw text between SELECT and FROM using token boundaries.
            // We reconstruct from the segment text using newline-split column extraction.
            string selectBody;
            if (fromPos > selectPos)
            {
                // Find character offsets of the SELECT+1 and FROM tokens in the segment
                // Use a simpler approach: find "SELECT" in segment text, then "FROM"
                selectBody = ExtractSelectBody(segment);
            }
            else
            {
                // No FROM found — take everything after SELECT (view with no FROM, e.g. SELECT 1)
                selectBody = segment;
            }

            var fields = ParseViewColumns(selectBody);

            result.Add(new TableDefinition
            {
                Name    = viewName,
                IsView  = true,
                Fields  = fields.AsReadOnly(),
            });

            searchFrom = nameStart + 1;
        }

        return result;
    }

    /// <summary>
    /// Extracts the raw SELECT column-list text from a view body segment
    /// (the text between SELECT and the first top-level FROM).
    /// </summary>
    private static string ExtractSelectBody(string segment)
    {
        // Find SELECT position (case-insensitive word boundary)
        var selectMatch = Regex.Match(segment, @"\bSELECT\b", RegexOptions.IgnoreCase);
        if (!selectMatch.Success) return string.Empty;

        int start = selectMatch.Index + selectMatch.Length;
        int depth = 0;
        bool inStr = false;

        for (int i = start; i < segment.Length - 3; i++)
        {
            char c = segment[i];
            if (inStr)
            {
                if (c == '\'' && i + 1 < segment.Length && segment[i + 1] == '\'') { i++; continue; }
                if (c == '\'') inStr = false;
                continue;
            }
            if (c == '\'') { inStr = true; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }

            if (depth == 0 &&
                i + 4 < segment.Length &&
                segment[i..].StartsWith("FROM", StringComparison.OrdinalIgnoreCase) &&
                (i == 0 || !char.IsLetterOrDigit(segment[i - 1])) &&
                (i + 4 >= segment.Length || !char.IsLetterOrDigit(segment[i + 4])))
            {
                return segment[start..i];
            }
        }
        return segment[start..];
    }

    /// <summary>
    /// Parses column names from a raw SELECT column-list body.
    /// Returns <see cref="FieldDefinition"/> entries with <c>type = "unknown"</c>.
    /// </summary>
    private static List<FieldDefinition> ParseViewColumns(string selectBody)
    {
        var fields = new List<FieldDefinition>();
        if (string.IsNullOrWhiteSpace(selectBody)) return fields;

        // Collapse newlines so multi-line expressions become single items, then split by
        // top-level commas.  SplitCheckCommas handles nested parens and string literals
        // (including N'…' Unicode strings that may contain internal commas).
        var collapsed = Regex.Replace(selectBody, @"\r?\n", " ");
        var items     = SplitCheckCommas(collapsed);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            // Strip surrounding whitespace and any leading comma (AdventureWorks leading-comma style)
            var expr = item.Trim().TrimStart(',').Trim();
            if (string.IsNullOrEmpty(expr)) continue;

            string? colName = ExtractViewColumnName(expr);
            if (colName is null || seen.Contains(colName)) continue;

            seen.Add(colName);
            fields.Add(new FieldDefinition
            {
                Name     = colName,
                Type     = "unknown",
                Nullable = true,
            });
        }
        return fields;
    }

    /// <summary>
    /// Extracts a single column name from one line of a SELECT column list.
    /// Tries three patterns in order:
    /// 1. Explicit alias:  <c>… AS [alias]</c> / <c>… AS alias</c>
    /// 2. Dotted ref:      <c>t.[col]</c> at end of line
    /// 3. Bare identifier: <c>[col]</c> or <c>col</c> (only line content)
    /// </summary>
    private static string? ExtractViewColumnName(string line)
    {
        // 1. AS alias (most reliable — handles complex expressions)
        var asMatch = s_viewAsAliasRx.Match(line);
        if (asMatch.Success)
        {
            for (int g = 1; g <= 4; g++)
                if (asMatch.Groups[g].Success) return asMatch.Groups[g].Value;
        }

        // 2. SQL Server assignment-style: [alias] = expr  or  alias = expr
        var assignMatch = s_viewAssignAliasRx.Match(line.TrimStart());
        if (assignMatch.Success)
        {
            for (int g = 1; g <= 2; g++)
                if (assignMatch.Groups[g].Success) return assignMatch.Groups[g].Value;
        }

        // 3. Dotted reference at end of trimmed line: alias.[col]
        var dotMatch = s_viewDotColRx.Match(line);
        if (dotMatch.Success)
        {
            for (int g = 1; g <= 4; g++)
                if (dotMatch.Groups[g].Success) return dotMatch.Groups[g].Value;
        }

        // 4. Bare column — the whole line is just [col] or col (possibly with leading comma)
        var bareMatch = s_viewBareColRx.Match(line);
        if (bareMatch.Success)
        {
            for (int g = 1; g <= 4; g++)
                if (bareMatch.Groups[g].Success) return bareMatch.Groups[g].Value;
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

    // Strips explicit precision from PostgreSQL timestamp/time types when no precision
    // was declared in the DDL. information_schema returns the type without precision;
    // pg_dump writes the default precision (6) explicitly. Storing without precision
    // matches what the introspector returns, avoiding spurious drift.
    private static readonly Regex s_pgTimestampPrecision = new(
        @"^(timestamp|time)\(\d+\)(.*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string NormalizePostgres(string raw)
    {
        var lower = raw.ToLowerInvariant().Trim();

        // Array suffix: normalise base type then re-append []
        // The parser produces "integer []" or "character varying []" etc.
        bool isArray = lower.EndsWith(" []") || lower.EndsWith("[]");
        if (isArray)
        {
            var baseStr = lower.EndsWith(" []") ? lower[..^3].Trim() : lower[..^2];
            return NormalizePostgresBase(baseStr) + "[]";
        }

        return NormalizePostgresBase(lower);
    }

    private static string NormalizePostgresBase(string lower)
    {
        // Serial shorthands
        if (lower == "serial")      return "integer";
        if (lower == "bigserial")   return "bigint";
        if (lower == "smallserial") return "smallint";

        // character varying → varchar  /  character(N) → char(N)
        if (lower == "character varying") return "varchar";
        if (lower.StartsWith("character varying(", StringComparison.Ordinal))
            return "varchar" + lower["character varying".Length..];
        if (lower == "character") return "char";
        if (lower.StartsWith("character(", StringComparison.Ordinal))
            return "char" + lower["character".Length..];

        // timestamp(N) / time(N) — strip explicit default precision
        // pg_dump writes e.g. "timestamp(6) without time zone"; information_schema
        // returns "timestamp without time zone" (no precision for the default).
        var m = s_pgTimestampPrecision.Match(lower);
        if (m.Success)
            return m.Groups[1].Value + m.Groups[2].Value; // e.g. "timestamp without time zone"

        // Bare temporal aliases → canonical PostgreSQL names (what pg_catalog / information_schema returns).
        // Without this, DDL-declared TIMESTAMP drifts against the introspector on every db drift run.
        if (lower == "timestamp")   return "timestamp without time zone";
        if (lower == "time")        return "time without time zone";
        if (lower == "timestamptz") return "timestamp with time zone";
        if (lower == "timetz")      return "time with time zone";

        return lower;
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

    // Matches space(N) where N is an integer literal — used to normalise SQL Server
    // DDL defaults to the form sys.default_constraints returns: space((N)).
    private static readonly Regex s_spaceArgRx = new(
        @"^space\((\d+)\)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches SQL Server unseparated date literals that must be stored quoted.
    //   YYYYMMDD                    e.g. 20000101
    //   YYYYMMDD HH:MM:SS           e.g. 29991231 23:59:59
    //   YYYYMMDD HH:MM:SS.fffffff   e.g. 20000101 00:00:00.0000000
    private static readonly Regex s_sqlServerDateLiteralRx = new(
        @"^\d{8}( \d{2}:\d{2}:\d{2}(\.\d+)?)?$",
        RegexOptions.Compiled);

    // Matches precision/scale groups, e.g. "(18, 0)" or "( 10 , 2 )" — strips inner spaces.
    private static readonly Regex s_precisionSpacingRx = new(
        @"\(\s*(\d+)\s*,\s*(\d+)\s*\)",
        RegexOptions.Compiled);

    private static string NormalizeSqlServer(string raw)
    {
        var lower = raw.Trim().ToLowerInvariant();

        // xml([schema].[collection]) — strip typed XML schema collection; introspector returns bare xml
        if (lower.StartsWith("xml(")) return "xml";

        // sysname is a SQL Server system alias for nvarchar(128)
        if (lower == "sysname") return "nvarchar(128)";

        // integer is an alias for int in T-SQL
        if (lower == "integer") return "int";

        // dec(...) is an alias for decimal(...) in T-SQL
        if (lower.StartsWith("dec(", StringComparison.Ordinal))
            lower = "decimal" + lower[3..];

        // Remove spaces inside precision/scale parentheses: decimal(18, 0) → decimal(18,0)
        lower = s_precisionSpacingRx.Replace(lower, "($1,$2)");

        // datetime2 / time / datetimeoffset without explicit precision default to scale 7
        if (lower == "datetime2")      return "datetime2(7)";
        if (lower == "time")           return "time(7)";
        if (lower == "datetimeoffset") return "datetimeoffset(7)";

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
    /// Returns <c>true</c> when a token signals the end of a SQL Server computed-column
    /// expression (<c>name AS expr [PERSISTED]</c>). Used to know when to stop
    /// collecting expression tokens.
    /// </summary>
    private static bool IsComputedExpressionTerminator(string t) =>
        t.Equals("PERSISTED",  StringComparison.OrdinalIgnoreCase) ||
        t.Equals("NOT",        StringComparison.OrdinalIgnoreCase) ||
        t.Equals("NULL",       StringComparison.OrdinalIgnoreCase) ||
        t.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("WITH",       StringComparison.OrdinalIgnoreCase) ||
        t.Equals("ON",         StringComparison.OrdinalIgnoreCase) ||
        t.Equals("FOR",        StringComparison.OrdinalIgnoreCase);

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
    /// <summary>
    /// Skips an optional SQL Server <c>WITH CHECK</c> or <c>WITH NOCHECK</c> clause that
    /// may appear between the table name and <c>ADD</c> in an <c>ALTER TABLE</c> statement.
    /// <code>ALTER TABLE [T] WITH CHECK ADD CONSTRAINT …</code>
    /// <code>ALTER TABLE [T] WITH NOCHECK ADD CONSTRAINT …</code>
    /// Does nothing if the next token is not <c>WITH</c>.
    /// </summary>
    private static void SkipWithCheckClause(List<string> tokens, ref int i)
    {
        if (i >= tokens.Count || !tokens[i].Equals("WITH", StringComparison.OrdinalIgnoreCase))
            return;
        i++; // consume WITH
        if (i < tokens.Count && (
            tokens[i].Equals("CHECK",   StringComparison.OrdinalIgnoreCase) ||
            tokens[i].Equals("NOCHECK", StringComparison.OrdinalIgnoreCase)))
            i++; // consume CHECK / NOCHECK
    }

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
        token.Equals("MODIFY",     StringComparison.OrdinalIgnoreCase) ||
        token.Equals("RENAME",     StringComparison.OrdinalIgnoreCase) ||
        token.Equals("WITH",       StringComparison.OrdinalIgnoreCase) ||
        token.Equals("SET",        StringComparison.OrdinalIgnoreCase) ||
        token.Equals("NOCHECK",    StringComparison.OrdinalIgnoreCase) ||
        token.Equals("ENABLE",     StringComparison.OrdinalIgnoreCase) ||
        token.Equals("DISABLE",    StringComparison.OrdinalIgnoreCase) ||
        // Batch separator (SQL Server)
        token.Equals("GO",         StringComparison.OrdinalIgnoreCase) ||
        // CREATE INDEX … ON table USING <method> — USING must not be consumed as schema qualifier
        token.Equals("USING",      StringComparison.OrdinalIgnoreCase) ||
        // CREATE TYPE [name] FROM base_type — FROM must not be consumed as schema qualifier
        token.Equals("FROM",       StringComparison.OrdinalIgnoreCase) ||
        // CREATE VIEW [name] AS SELECT … — AS must not be consumed as schema qualifier
        token.Equals("AS",         StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string s) =>
        s.Length > 80 ? s[..77] + "..." : s;
}
