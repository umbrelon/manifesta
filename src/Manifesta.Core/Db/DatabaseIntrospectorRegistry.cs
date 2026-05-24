namespace Manifesta.Core;

public static class DatabaseIntrospectorRegistry
{
    private static IDatabaseIntrospectorFactory? _factory;

    public static void Register(IDatabaseIntrospectorFactory factory)
        => _factory = factory;

    public static IDatabaseIntrospectorFactory GetFactory()
        => _factory ?? throw new ManifestaConfigException(
            "'init db' and 'db' commands require a database provider. " +
            "Ensure you are using the full edition of Manifesta.");
}
