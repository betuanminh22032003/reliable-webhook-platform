using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using ReliableWebhook.Application;
using ReliableWebhook.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 1_048_576);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_048_576);
builder
    .Services.AddHealthChecks()
    .AddAsyncCheck(
        "postgres",
        async cancellationToken =>
        {
            try
            {
                await using var dataSource = NpgsqlDataSource.Create(
                    builder.Configuration["Database:ConnectionString"]!
                );
                await using var connection = await dataSource.OpenConnectionAsync(
                    cancellationToken
                );
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (Exception exception)
            {
                return HealthCheckResult.Unhealthy("PostgreSQL unavailable", exception);
            }
        },
        tags: ["ready"]
    );
var app = builder.Build();
app.UseExceptionHandler(handler =>
    handler.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        context.Response.StatusCode = exception is IdempotencyConflictException ? 409 : 500;
        await context.Response.WriteAsJsonAsync(
            new
            {
                error = exception is IdempotencyConflictException
                    ? exception.Message
                    : "An unexpected error occurred.",
            }
        );
    })
);
await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
var endpoints = app.MapGroup("/api/webhook-endpoints");
endpoints.MapPost(
    "",
    async (CreateEndpoint request, IWebhookStore store, CancellationToken cancellationToken) =>
    {
        if (
            !DestinationUrlValidator.IsValid(request.DestinationUrl)
            || string.IsNullOrWhiteSpace(request.Secret)
            || request.Secret.Length < 16
        )
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    {
                        "request",
                        ["A valid HTTP(S) URL and secret of at least 16 characters are required."]
                    },
                }
            );
        var created = await store.CreateEndpointAsync(request, cancellationToken);
        return Results.Created($"/api/webhook-endpoints/{created.Id}", created);
    }
);
endpoints.MapGet(
    "/{id:guid}",
    async (Guid id, IWebhookStore store, CancellationToken cancellationToken) =>
        (await store.GetEndpointAsync(id, cancellationToken)) is { } endpoint
            ? Results.Ok(endpoint)
            : Results.NotFound()
);
endpoints.MapGet(
    "",
    async (IWebhookStore store, CancellationToken cancellationToken) =>
        Results.Ok(await store.ListEndpointsAsync(cancellationToken))
);
endpoints.MapDelete(
    "/{id:guid}",
    async (Guid id, IWebhookStore store, CancellationToken cancellationToken) =>
        await store.DeleteEndpointAsync(id, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound()
);
app.MapPost(
    "/api/events",
    async (PublishEvent request, IWebhookStore store, CancellationToken cancellationToken) =>
    {
        if (
            string.IsNullOrWhiteSpace(request.EventType)
            || request.EventType.Length > 200
            || request.IdempotencyKey?.Length > 200
            || request.Payload.ValueKind is JsonValueKind.Undefined
        )
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    {
                        "request",
                        ["Event type (maximum 200 characters) and a JSON payload are required."]
                    },
                }
            );
        var result = await store.PublishAsync(request, cancellationToken);
        return result.IsDuplicate
            ? Results.Ok(result.Event)
            : Results.Created($"/api/events/{result.Event.Id}", result.Event);
    }
);
app.MapGet(
    "/api/events/{id:guid}",
    async (Guid id, IWebhookStore store, CancellationToken cancellationToken) =>
        (await store.GetEventAsync(id, cancellationToken)) is { } webhookEvent
            ? Results.Ok(webhookEvent)
            : Results.NotFound()
);
app.MapGet(
    "/api/events/{id:guid}/deliveries",
    async (Guid id, IWebhookStore store, CancellationToken cancellationToken) =>
        Results.Ok(await store.GetDeliveriesAsync(id, cancellationToken))
);
app.MapGet(
    "/api/deliveries/{id:guid}",
    async (Guid id, IWebhookStore store, CancellationToken cancellationToken) =>
        (await store.GetDeliveryAsync(id, cancellationToken)) is { } detail
            ? Results.Ok(new { detail.Delivery, detail.Attempts })
            : Results.NotFound()
);
app.MapHealthChecks("/health", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = x => x.Tags.Contains("ready") });
app.Run();

public partial class Program;
