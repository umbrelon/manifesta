using MySqlConnector;
using Testcontainers.MySql;
using Xunit;

namespace Manifesta.Providers.Tests;

/// <summary>
/// Shared fixture for MySQL Testcontainers.
/// Creates a 'testdb' database with representative tables, views, FKs, generated columns,
/// default values, and varied MySQL type coverage.
/// Table names are bare (no schema prefix), matching MySQL provider output.
/// </summary>
public class MySqlTestFixture : IAsyncLifetime
{
    private MySqlContainer? _container;
    private string? _connectionString;

    public string ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("Container not initialized");

    async Task IAsyncLifetime.InitializeAsync()
    {
        _container = new MySqlBuilder()
            .WithDatabase("testdb")
            .WithUsername("root")
            .WithPassword("Test@1234567")
            .Build();

        await _container.StartAsync();

        _connectionString = _container.GetConnectionString();

        await InitializeTestDatabaseAsync();
        await CreateViewAsync();
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
        using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();

        var script = @"
            DROP TABLE IF EXISTS Bundle;
            DROP TABLE IF EXISTS BundleType;
            DROP TABLE IF EXISTS BundlePurchaseType;
            DROP TABLE IF EXISTS Settings;

            CREATE TABLE BundleType (
                Id   INT AUTO_INCREMENT PRIMARY KEY,
                Name VARCHAR(100) NOT NULL
            );

            CREATE TABLE BundlePurchaseType (
                Id   INT AUTO_INCREMENT PRIMARY KEY,
                Name VARCHAR(100) NOT NULL
            );

            CREATE TABLE Bundle (
                lBundleID        INT AUTO_INCREMENT PRIMARY KEY,
                szBundle         VARCHAR(20)    NOT NULL,
                szDescription    VARCHAR(50)    NULL,
                cPriority        INT            NOT NULL,
                cTypeBundle      INT            NOT NULL,
                dAvailableFrom   DATETIME       NULL,
                lfTotalAmount    DECIMAL(18,2)  NOT NULL,
                Price            DECIMAL(18,2)  NOT NULL,
                TaxPercentage    DECIMAL(5,2)   NULL,
                IsActive         TINYINT(1)     NOT NULL DEFAULT 1,
                BundlePurchaseTypeID INT        NOT NULL,
                TotalWithTax     DECIMAL(20,4)  GENERATED ALWAYS AS (lfTotalAmount * (1 + COALESCE(TaxPercentage, 0) / 100)) VIRTUAL,
                BundleCode       VARCHAR(30)    GENERATED ALWAYS AS (CONCAT(szBundle, '-', CAST(cPriority AS CHAR))) STORED,
                FOREIGN KEY (cTypeBundle)        REFERENCES BundleType(Id)        ON DELETE CASCADE,
                FOREIGN KEY (BundlePurchaseTypeID) REFERENCES BundlePurchaseType(Id)
            );

            CREATE TABLE Settings (
                SettingId    INT              AUTO_INCREMENT PRIMARY KEY,
                SettingKey   VARCHAR(100)     NOT NULL UNIQUE,
                SettingValue TEXT             NULL,
                CreatedDate  DATETIME         DEFAULT NOW()
            );

            -- LargeTypes exercises ulong overflow fixes in the introspector:
            --   LONGTEXT  → CHARACTER_MAXIMUM_LENGTH = 4294967295 (overflows Int32)
            --   BIGINT UNSIGNED → can hold values > Int32.MaxValue
            CREATE TABLE LargeTypes (
                Id          BIGINT UNSIGNED  AUTO_INCREMENT PRIMARY KEY,
                Content     LONGTEXT         NULL,
                BigValue    BIGINT UNSIGNED  NOT NULL DEFAULT 0
            );

            -- Non-unique regular index (also exercises that PK is excluded)
            CREATE INDEX idx_bundle_cpriority ON Bundle(cPriority);

            -- CHECK constraints (MySQL 8.0.16+)
            ALTER TABLE Bundle ADD CONSTRAINT chk_bundle_price_positive CHECK (Price > 0);
            ALTER TABLE Bundle ADD CONSTRAINT chk_bundle_amounts_valid   CHECK (Price <= lfTotalAmount);
        ";

        using var command = connection.CreateCommand();
        command.CommandText = script;
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync();

        var insertScript = @"
            INSERT INTO BundleType (Name)         VALUES ('Standard'), ('Premium'), ('Basic');
            INSERT INTO BundlePurchaseType (Name)  VALUES ('OneTime'), ('Recurring'), ('Trial');
            INSERT INTO Bundle (szBundle, szDescription, cPriority, cTypeBundle, lfTotalAmount, Price, TaxPercentage, IsActive, BundlePurchaseTypeID)
            VALUES
                ('Bundle1', 'Test Bundle 1', 1, 1, 99.99,  99.99,  21.00, 1, 1),
                ('Bundle2', NULL,            2, 2, 49.99,  49.99,  NULL,  1, 2);
            INSERT INTO Settings (SettingKey, SettingValue)
            VALUES ('AppVersion', '1.0.0'), ('DatabaseVersion', '2024-05-07');
        ";

        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = insertScript;
        insertCmd.CommandTimeout = 30;
        await insertCmd.ExecuteNonQueryAsync();
    }

    private async Task CreateViewAsync()
    {
        using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();

        using var dropCmd = connection.CreateCommand();
        dropCmd.CommandText = "DROP VIEW IF EXISTS BundleView";
        await dropCmd.ExecuteNonQueryAsync();

        using var createCmd = connection.CreateCommand();
        createCmd.CommandText = @"
            CREATE VIEW BundleView AS
            SELECT lBundleID, szBundle, szDescription, cPriority
            FROM Bundle";
        await createCmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("MySQL Collection")]
public class MySqlCollection : ICollectionFixture<MySqlTestFixture>
{
    // Defines the collection — no code needed
}
