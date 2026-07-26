using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using ReliableWebhook.Application;
using ReliableWebhook.Infrastructure;

var builder = WebApplication.CreateBuilder(args); builder.Services.AddInfrastructure(builder.Configuration); builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 1_048_576); builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_048_576); builder.Services.AddHealthChecks().AddAsyncCheck("postgres", async ct => { try { await using var ds = NpgsqlDataSource.Create(builder.Configuration["Database:ConnectionString"]!); await using var c = await ds.OpenConnectionAsync(ct); await using var cmd = new NpgsqlCommand("SELECT 1", c); await cmd.ExecuteScalarAsync(ct); return HealthCheckResult.Healthy(); } catch (Exception ex) { return HealthCheckResult.Unhealthy("PostgreSQL unavailable", ex); } }, tags: ["ready"]); var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async c => { var ex = c.Features.Get<IExceptionHandlerFeature>()?.Error; c.Response.StatusCode = ex is IdempotencyConflictException ? 409 : 500; await c.Response.WriteAsJsonAsync(new { error = ex is IdempotencyConflictException ? ex.Message : "An unexpected error occurred." }); }));
await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
var endpoints = app.MapGroup("/api/webhook-endpoints");
endpoints.MapPost("", async (CreateEndpoint r, IWebhookStore s, CancellationToken ct) => { if (!DestinationUrlValidator.IsValid(r.DestinationUrl) || string.IsNullOrWhiteSpace(r.Secret) || r.Secret.Length < 16) return Results.ValidationProblem(new Dictionary<string, string[]> { { "request", ["A valid HTTP(S) URL and secret of at least 16 characters are required."] } }); var x = await s.CreateEndpointAsync(r, ct); return Results.Created($"/api/webhook-endpoints/{x.Id}", x); });
endpoints.MapGet("/{id:guid}", async (Guid id, IWebhookStore s, CancellationToken ct) => (await s.GetEndpointAsync(id, ct)) is { } x ? Results.Ok(x) : Results.NotFound()); endpoints.MapGet("", async (IWebhookStore s, CancellationToken ct) => Results.Ok(await s.ListEndpointsAsync(ct))); endpoints.MapDelete("/{id:guid}", async (Guid id, IWebhookStore s, CancellationToken ct) => await s.DeleteEndpointAsync(id, ct) ? Results.NoContent() : Results.NotFound());
app.MapPost("/api/events", async (PublishEvent r, IWebhookStore s, CancellationToken ct) => { if (string.IsNullOrWhiteSpace(r.EventType) || r.EventType.Length > 200 || r.IdempotencyKey?.Length > 200 || r.Payload.ValueKind is JsonValueKind.Undefined) return Results.ValidationProblem(new Dictionary<string, string[]> { { "request", ["Event type (maximum 200 characters) and a JSON payload are required."] } }); var x = await s.PublishAsync(r, ct); return x.IsDuplicate ? Results.Ok(x.Event) : Results.Created($"/api/events/{x.Event.Id}", x.Event); });
app.MapGet("/api/events/{id:guid}", async (Guid id, IWebhookStore s, CancellationToken ct) => (await s.GetEventAsync(id, ct)) is { } x ? Results.Ok(x) : Results.NotFound()); app.MapGet("/api/events/{id:guid}/deliveries", async (Guid id, IWebhookStore s, CancellationToken ct) => Results.Ok(await s.GetDeliveriesAsync(id, ct))); app.MapGet("/api/deliveries/{id:guid}", async (Guid id, IWebhookStore s, CancellationToken ct) => (await s.GetDeliveryAsync(id, ct)) is { } x ? Results.Ok(new { x.Delivery, x.Attempts }) : Results.NotFound());
app.MapHealthChecks("/health", new() { Predicate = _ => false }); app.MapHealthChecks("/health/ready", new() { Predicate = x => x.Tags.Contains("ready") }); app.Run();
public partial class Program;
