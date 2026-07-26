using Xunit;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using Npgsql;
using ReliableWebhook.Application;
using ReliableWebhook.Infrastructure;
using Testcontainers.PostgreSql;
namespace ReliableWebhook.IntegrationTests;

public sealed class PostgresTests : IAsyncLifetime
{
    readonly PostgreSqlContainer db = new PostgreSqlBuilder("postgres:17-alpine").Build(); PostgresWebhookStore store = null!; public async ValueTask InitializeAsync() { await db.StartAsync(); var ds = NpgsqlDataSource.Create(db.GetConnectionString()); await new DatabaseMigrator(ds).MigrateAsync(); store = new(ds); }
    public async ValueTask DisposeAsync() => await db.DisposeAsync();
    [Fact] public async Task PersistsEndpointEventOutboxAndIdempotency() { var ep = await store.CreateEndpointAsync(new("https://example.com", "1234567890123456"), default); Assert.NotNull(await store.GetEndpointAsync(ep.Id, default)); using var json = JsonDocument.Parse("{\"x\":1}"); var first = await store.PublishAsync(new("thing.created", json.RootElement, "key-1"), default); var second = await store.PublishAsync(new("thing.created", json.RootElement, "key-1"), default); Assert.Equal(first.Event.Id, second.Event.Id); Assert.True(second.IsDuplicate); var deliveries = await store.GetDeliveriesAsync(first.Event.Id, default); Assert.Single(deliveries); var claimed = await store.ClaimAsync(10, TimeSpan.FromMinutes(1), 3, default); Assert.Single(claimed); Assert.Empty(await store.ClaimAsync(10, TimeSpan.FromMinutes(1), 3, default)); await store.CompleteAsync(claimed[0], 1, 204, 12, default); var detail = await store.GetDeliveryAsync(claimed[0].Id, default); Assert.Equal(ReliableWebhook.Domain.DeliveryStatus.Succeeded, detail!.Value.Item1.Status); Assert.Single(detail.Value.Item2); }
    [Fact] public async Task ApiLivenessSurfaceWorks() { await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { { "Database:ConnectionString", db.GetConnectionString() } }))); using var client = app.CreateClient(); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode); }
}
