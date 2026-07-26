using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ReliableWebhook.Application;
using ReliableWebhook.Domain;
using ReliableWebhook.Infrastructure;
using Xunit;

namespace ReliableWebhook.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresTests(PostgresFixture database) : IAsyncLifetime
{
    private PostgresWebhookStore store = null!;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        store = new PostgresWebhookStore(database.DataSource);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task EndpointPersistenceRoundTripsWithoutExposingSecret()
    {
        var created = await store.CreateEndpointAsync(
            new CreateEndpoint("https://example.com/webhook", "1234567890123456"),
            TestContext.Current.CancellationToken);

        var persisted = await store.GetEndpointAsync(created.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(persisted);
        Assert.Equal(created.DestinationUrl, persisted.DestinationUrl);
        Assert.DoesNotContain("Secret", persisted.GetType().GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task PublishingCreatesEventDeliveryAndOutboxTransactionally()
    {
        await CreateEndpointAsync();
        using var payload = JsonDocument.Parse("{\"invoiceId\":\"inv-1\"}");

        var published = await store.PublishAsync(
            new PublishEvent("invoice.paid", payload.RootElement, null),
            TestContext.Current.CancellationToken);
        var deliveries = await store.GetDeliveriesAsync(published.Event.Id, TestContext.Current.CancellationToken);
        var claimed = await store.ClaimAsync(10, TimeSpan.FromMinutes(1), 3, TestContext.Current.CancellationToken);

        Assert.False(published.IsDuplicate);
        Assert.Single(deliveries);
        Assert.Single(claimed);
        Assert.Equal(deliveries[0].Id, claimed[0].Id);
    }

    [Fact]
    public async Task EquivalentIdempotentPublishesReturnOriginalEvent()
    {
        using var payload = JsonDocument.Parse("{\"value\":1}");
        var request = new PublishEvent("thing.created", payload.RootElement, "stable-key");

        var first = await store.PublishAsync(request, TestContext.Current.CancellationToken);
        var second = await store.PublishAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(first.Event.Id, second.Event.Id);
        Assert.True(second.IsDuplicate);
    }

    [Fact]
    public async Task ChangedIdempotentPublishIsRejected()
    {
        using var firstPayload = JsonDocument.Parse("{\"value\":1}");
        using var changedPayload = JsonDocument.Parse("{\"value\":2}");
        await store.PublishAsync(
            new PublishEvent("thing.created", firstPayload.RootElement, "conflicting-key"),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() => store.PublishAsync(
            new PublishEvent("thing.created", changedPayload.RootElement, "conflicting-key"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentClaimsNeverReturnSameDelivery()
    {
        await CreateEndpointAsync();
        using var payload = JsonDocument.Parse("{\"value\":1}");
        await store.PublishAsync(new PublishEvent("thing.created", payload.RootElement, null), TestContext.Current.CancellationToken);

        var claims = await Task.WhenAll(
            store.ClaimAsync(1, TimeSpan.FromMinutes(1), 3, TestContext.Current.CancellationToken),
            store.ClaimAsync(1, TimeSpan.FromMinutes(1), 3, TestContext.Current.CancellationToken));

        Assert.Equal(1, claims.Sum(result => result.Count));
    }

    [Fact]
    public async Task DeliveryCompletionPersistsStatusAndAttempt()
    {
        await CreateEndpointAsync();
        using var payload = JsonDocument.Parse("{\"value\":1}");
        var published = await store.PublishAsync(new PublishEvent("thing.created", payload.RootElement, null), TestContext.Current.CancellationToken);
        var claimed = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1), 3, TestContext.Current.CancellationToken));

        await store.CompleteAsync(claimed, 1, 204, 12, TestContext.Current.CancellationToken);
        var detail = await store.GetDeliveryAsync(claimed.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.Equal(DeliveryStatus.Succeeded, detail.Value.Delivery.Status);
        Assert.Equal(204, detail.Value.Delivery.LastStatusCode);
        Assert.Single(detail.Value.Attempts);
        Assert.Equal(published.Event.Id, detail.Value.Delivery.EventId);
    }

    [Fact]
    public async Task ApiEndpointCreationCoversHttpSurface()
    {
        await using var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })));
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/webhook-endpoints",
            new CreateEndpoint("https://receiver.example/webhooks", "1234567890123456"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("receiver.example", body);
        Assert.Contains("1234567890123456", body);
    }

    private Task<CreatedEndpoint> CreateEndpointAsync() => store.CreateEndpointAsync(
        new CreateEndpoint("https://example.com/webhook", "1234567890123456"),
        TestContext.Current.CancellationToken);
}
