using Manifesta.Core.IR;

namespace Manifesta.Core.Ddl;

public static class DdlTableLoader
{
    public static IReadOnlyList<TableDefinition> Load(
        IEnumerable<string> filePaths,
        DbProvider          provider,
        string?             schemaPrefix = null)
    {
        var allTables = new Dictionary<string, TableDefinition>(TableNames.Comparer);
        var allErrors = new List<string>();

        foreach (var path in filePaths)
        {
            if (!File.Exists(path))
                throw new ManifestaConfigException($"DDL file not found: {path}");

            var sql    = File.ReadAllText(path);
            var result = new SqlDdlParser().Parse(sql, provider, schemaPrefix);

            if (result.Errors.Count > 0)
                allErrors.AddRange(result.Errors.Select(e => $"{Path.GetFileName(path)}: {e}"));

            foreach (var table in result.Tables)
            {
                if (allTables.ContainsKey(table.Name))
                    throw new ManifestaConfigException(
                        $"Duplicate table '{table.Name}' found across DDL files.");
                allTables[table.Name] = table;
            }
        }

        if (allErrors.Count > 0)
            throw new ManifestaConfigException(
                $"DDL parse errors:{Environment.NewLine}{string.Join(Environment.NewLine, allErrors)}");

        return allTables.Values.ToList().AsReadOnly();
    }
}
