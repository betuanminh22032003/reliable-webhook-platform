using Npgsql;
using ReliableWebhook.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ReliableWebhook.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;

    public string ConnectionString { get; private set; } = string.Empty;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION_STRING") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithCleanUp(true)
                .Build();
            await container.StartAsync(TestContext.Current.CancellationToken);
            ConnectionString = container.GetConnectionString();
        }

        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await new DatabaseMigrator(DataSource).MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask ResetAsync()
    {
        const string sql = """
            TRUNCATE TABLE delivery_attempts, delivery_outbox, deliveries, webhook_events, webhook_endpoints CASCADE;
            """;
        await using var connection = await DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL integration";
}
