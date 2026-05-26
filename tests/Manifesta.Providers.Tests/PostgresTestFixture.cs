using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Manifesta.Providers.Tests;

/// <summary>
/// Shared fixture for PostgreSQL Testcontainers.
/// Creates a 'testdb' database with representative tables, views, FKs, a generated (STORED) column,
/// default values, and varied PostgreSQL type coverage — spread across two schemas (public and app)
/// to exercise schema filtering.
/// Table names are schema-qualified (e.g. public.bundle), matching PostgreSQL provider output.
/// </summary>
public class PostgresTestFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _connectionString;

    public string ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("Container not initialized");

    async Task IAsyncLifetime.InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithDatabase("testdb")
            .WithUsername("postgres")
            .WithPassword("Test@1234567")
            .Build();

        await _container.StartAsync();

        _connectionString = _container.GetConnectionString();

        await InitializeTestDatabaseAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_container != null)
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
        }
    }

    private async Task InitializeTestDatabaseAsync()
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        var schema = @"
            DROP VIEW  IF EXISTS public.bundleview;
            DROP TABLE IF EXISTS public.bundle         CASCADE;
            DROP TABLE IF EXISTS public.bundletype     CASCADE;
            DROP TABLE IF EXISTS public.bundlepurchasetype CASCADE;
            DROP TABLE IF EXISTS public.settings       CASCADE;
            DROP SCHEMA IF EXISTS app CASCADE;

            CREATE SCHEMA app;

            CREATE TABLE public.bundletype (
                id   SERIAL        PRIMARY KEY,
                name VARCHAR(100)  NOT NULL
            );

            CREATE TABLE public.bundlepurchasetype (
                id   SERIAL        PRIMARY KEY,
                name VARCHAR(100)  NOT NULL
            );

            CREATE TABLE public.bundle (
                lbundleid            SERIAL        PRIMARY KEY,
                szbundle             VARCHAR(20)   NOT NULL,
                szdescription        VARCHAR(50)   NULL,
                cpriority            INTEGER       NOT NULL,
                ctypebundle          INTEGER       NOT NULL,
                davailablefrom       TIMESTAMP     NULL,
                lftotalamount        NUMERIC(18,2) NOT NULL,
                price                NUMERIC(18,2) NOT NULL,
                taxpercentage        NUMERIC(5,2)  NULL,
                isactive             BOOLEAN       NOT NULL DEFAULT true,
                bundlepurchasetypeid INTEGER       NOT NULL,
                bundlecode           VARCHAR(30)   GENERATED ALWAYS AS (szbundle || '-' || cpriority::text) STORED,
                FOREIGN KEY (ctypebundle)          REFERENCES public.bundletype(id)         ON DELETE CASCADE,
                FOREIGN KEY (bundlepurchasetypeid) REFERENCES public.bundlepurchasetype(id)
            );

            CREATE TABLE public.settings (
                settingid    SERIAL       PRIMARY KEY,
                settingkey   VARCHAR(100) NOT NULL UNIQUE,
                settingvalue TEXT         NULL,
                createddate  TIMESTAMP    DEFAULT NOW()
            );

            CREATE VIEW public.bundleview AS
            SELECT lbundleid, szbundle, szdescription, cpriority FROM public.bundle;

            CREATE TABLE app.appsetting (
                id    SERIAL       PRIMARY KEY,
                key   VARCHAR(50)  NOT NULL,
                value TEXT         NULL
            );

            -- Non-unique regular index
            CREATE INDEX idx_bundle_cpriority ON public.bundle(cpriority);

            -- Partial (filtered) index
            CREATE INDEX idx_bundle_active_names ON public.bundle(szbundle) WHERE isactive = true;

            -- Covered index with INCLUDE columns (PostgreSQL 11+)
            CREATE INDEX idx_bundle_type_cover ON public.bundle(ctypebundle) INCLUDE (szbundle, price);

            -- Single-column check constraint (column = 'price')
            ALTER TABLE public.bundle ADD CONSTRAINT chk_bundle_price_positive CHECK (price > 0);

            -- Multi-column / table-level check constraint (column = NULL)
            ALTER TABLE public.bundle ADD CONSTRAINT chk_bundle_amounts_valid CHECK (price <= lftotalamount);
        ";

        using var schemaCmd = new NpgsqlCommand(schema, connection);
        schemaCmd.CommandTimeout = 30;
        await schemaCmd.ExecuteNonQueryAsync();

        var inserts = @"
            INSERT INTO public.bundletype (name) VALUES ('Standard'), ('Premium'), ('Basic');
            INSERT INTO public.bundlepurchasetype (name) VALUES ('OneTime'), ('Recurring'), ('Trial');
            INSERT INTO public.bundle
                (szbundle, szdescription, cpriority, ctypebundle, lftotalamount, price, taxpercentage, isactive, bundlepurchasetypeid)
            VALUES
                ('Bundle1', 'Test Bundle 1', 1, 1, 99.99, 99.99, 21.00, true,  1),
                ('Bundle2', NULL,            2, 2, 49.99, 49.99, NULL,  true,  2);
            INSERT INTO public.settings (settingkey, settingvalue)
            VALUES ('AppVersion', '1.0.0'), ('DatabaseVersion', '2024-05-07');
        ";

        using var insertCmd = new NpgsqlCommand(inserts, connection);
        insertCmd.CommandTimeout = 30;
        await insertCmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("PostgreSQL Collection")]
public class PostgreSqlCollection : ICollectionFixture<PostgresTestFixture>
{
    // Defines the collection — no code needed
}
