using System.Data;
using System.Reflection;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using ReliableWebhook.Application;
using ReliableWebhook.Domain;

namespace ReliableWebhook.Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public string ConnectionString { get; set; } = "";
}

public static class InfrastructureRegistration
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration config
    )
    {
        services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.SectionName));
        services.AddSingleton<NpgsqlDataSource>(sp =>
            NpgsqlDataSource.Create(
                sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString
            )
        );
        services.AddScoped<IWebhookStore, PostgresWebhookStore>();
        services.AddSingleton<DatabaseMigrator>();
        return services;
    }
}

public sealed class DatabaseMigrator(NpgsqlDataSource dataSource)
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var sql = await new StreamReader(
            Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream(
                    "ReliableWebhook.Infrastructure.Migrations.001_initial.sql"
                )!
        ).ReadToEndAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        await c.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO schema_migrations(version) VALUES(1) ON CONFLICT DO NOTHING",
                cancellationToken: ct
            )
        );
    }
}

public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException()
        : base("The idempotency key was already used with different content.") { }
}

public sealed class PostgresWebhookStore(NpgsqlDataSource dataSource) : IWebhookStore
{
    static PostgresWebhookStore()
    {
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new DeliveryStatusHandler());
    }

    public async Task<CreatedEndpoint> CreateEndpointAsync(CreateEndpoint r, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO webhook_endpoints VALUES(@id,@url,@secret,@enabled,@now,@now)",
                new
                {
                    id,
                    url = r.DestinationUrl,
                    secret = r.Secret,
                    enabled = r.Enabled,
                    now,
                },
                cancellationToken: ct
            )
        );
        return new(id, r.DestinationUrl, r.Secret, r.Enabled, now, now);
    }

    public async Task<WebhookEndpoint?> GetEndpointAsync(Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<WebhookEndpoint>(
            new CommandDefinition(
                "SELECT id,destination_url DestinationUrl,enabled,created_at CreatedAt,updated_at UpdatedAt FROM webhook_endpoints WHERE id=@id",
                new { id },
                cancellationToken: ct
            )
        );
    }

    public async Task<IReadOnlyList<WebhookEndpoint>> ListEndpointsAsync(CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (
            await c.QueryAsync<WebhookEndpoint>(
                new CommandDefinition(
                    "SELECT id,destination_url DestinationUrl,enabled,created_at CreatedAt,updated_at UpdatedAt FROM webhook_endpoints ORDER BY created_at",
                    cancellationToken: ct
                )
            )
        ).AsList();
    }

    public async Task<bool> DeleteEndpointAsync(Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE webhook_endpoints SET enabled=false,updated_at=now() WHERE id=@id AND enabled",
                    new { id },
                    cancellationToken: ct
                )
            ) > 0;
    }

    public async Task<PublishResult> PublishAsync(PublishEvent r, CancellationToken ct)
    {
        var hash = PayloadHasher.Hash(r.EventType, r.Payload);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        if (r.IdempotencyKey is not null)
        {
            var old = await c.QuerySingleOrDefaultAsync<WebhookEvent>(
                new CommandDefinition(
                    "SELECT id,event_type EventType,payload::text Payload,idempotency_key IdempotencyKey,payload_hash PayloadHash,created_at CreatedAt FROM webhook_events WHERE idempotency_key=@key FOR UPDATE",
                    new { key = r.IdempotencyKey },
                    tx,
                    cancellationToken: ct
                )
            );
            if (old is not null)
            {
                await tx.CommitAsync(ct);
                if (old.PayloadHash != hash)
                    throw new IdempotencyConflictException();
                return new(old, true);
            }
        }
        var now = DateTimeOffset.UtcNow;
        var e = new WebhookEvent(
            Guid.NewGuid(),
            r.EventType,
            r.Payload.GetRawText(),
            r.IdempotencyKey,
            hash,
            now
        );
        try
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO webhook_events VALUES(@Id,@EventType,CAST(@Payload AS jsonb),@IdempotencyKey,@PayloadHash,@CreatedAt)",
                    e,
                    tx,
                    cancellationToken: ct
                )
            );
        }
        catch (PostgresException x)
            when (x.SqlState == PostgresErrorCodes.UniqueViolation && r.IdempotencyKey is not null)
        {
            await tx.RollbackAsync(ct);
            return await PublishAsync(r, ct);
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                "WITH d AS (INSERT INTO deliveries(id,event_id,endpoint_id,status,attempt_count,max_attempts,next_attempt_at,created_at,updated_at) SELECT gen_random_uuid(),@id,e.id,'Pending',0,10,@now,@now,@now FROM webhook_endpoints e WHERE enabled RETURNING id) INSERT INTO delivery_outbox SELECT id,@now,@now FROM d",
                new { id = e.Id, now },
                tx,
                cancellationToken: ct
            )
        );
        await tx.CommitAsync(ct);
        return new(e, false);
    }

    public async Task<WebhookEvent?> GetEventAsync(Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<WebhookEvent>(
            new CommandDefinition(
                "SELECT id,event_type EventType,payload::text Payload,idempotency_key IdempotencyKey,payload_hash PayloadHash,created_at CreatedAt FROM webhook_events WHERE id=@id",
                new { id },
                cancellationToken: ct
            )
        );
    }

    public async Task<IReadOnlyList<Delivery>> GetDeliveriesAsync(
        Guid eventId,
        CancellationToken ct
    )
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (
            await c.QueryAsync<Delivery>(
                new CommandDefinition(
                    DeliverySql + " WHERE event_id=@eventId ORDER BY created_at",
                    new { eventId },
                    cancellationToken: ct
                )
            )
        ).AsList();
    }

    public async Task<(
        Delivery Delivery,
        IReadOnlyList<DeliveryAttempt> Attempts
    )?> GetDeliveryAsync(Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var d = await c.QuerySingleOrDefaultAsync<Delivery>(
            new CommandDefinition(DeliverySql + " WHERE id=@id", new { id }, cancellationToken: ct)
        );
        if (d is null)
            return null;
        var a = (
            await c.QueryAsync<DeliveryAttempt>(
                new CommandDefinition(
                    "SELECT id,delivery_id DeliveryId,attempt_number AttemptNumber,attempted_at AttemptedAt,status_code StatusCode,error,duration_ms DurationMs FROM delivery_attempts WHERE delivery_id=@id ORDER BY attempt_number",
                    new { id },
                    cancellationToken: ct
                )
            )
        ).AsList();
        return (d, a);
    }

    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(
        int count,
        TimeSpan lease,
        int maxAttempts,
        CancellationToken ct
    )
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (
            await c.QueryAsync<ClaimedDelivery>(
                new CommandDefinition(
                    "WITH picked AS (SELECT d.id FROM deliveries d JOIN delivery_outbox o ON o.delivery_id=d.id WHERE d.status IN ('Pending','Processing') AND o.available_at<=now() AND (d.lease_expires_at IS NULL OR d.lease_expires_at<now()) ORDER BY o.available_at FOR UPDATE SKIP LOCKED LIMIT @count), claimed AS (UPDATE deliveries d SET status='Processing',lease_token=gen_random_uuid(),lease_expires_at=now()+@lease,max_attempts=@maxAttempts,updated_at=now() FROM picked WHERE d.id=picked.id RETURNING d.*) SELECT c.id,e.destination_url DestinationUrl,e.secret,w.event_type EventType,w.payload::text Payload,c.attempt_count AttemptCount,c.max_attempts MaxAttempts,c.lease_token LeaseToken FROM claimed c JOIN webhook_endpoints e ON e.id=c.endpoint_id JOIN webhook_events w ON w.id=c.event_id",
                    new
                    {
                        count,
                        lease,
                        maxAttempts,
                    },
                    cancellationToken: ct
                )
            )
        ).AsList();
    }

    public Task CompleteAsync(ClaimedDelivery i, int n, int code, int ms, CancellationToken ct) =>
        Finish(i, n, code, null, ms, DateTimeOffset.UtcNow, true, false, ct);

    public Task FailAsync(
        ClaimedDelivery i,
        int n,
        int? code,
        string error,
        int ms,
        DateTimeOffset next,
        bool terminal,
        CancellationToken ct
    ) => Finish(i, n, code, error, ms, next, false, terminal, ct);

    async Task Finish(
        ClaimedDelivery i,
        int n,
        int? code,
        string? error,
        int ms,
        DateTimeOffset next,
        bool success,
        bool terminal,
        CancellationToken ct
    )
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var changed = await c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE deliveries SET status=@status,attempt_count=@n,next_attempt_at=@next,completed_at=CASE WHEN @success THEN now() ELSE NULL END,last_status_code=@code,last_error=@error,lease_token=NULL,lease_expires_at=NULL,updated_at=now() WHERE id=@id AND lease_token=@token",
                new
                {
                    id = i.Id,
                    token = i.LeaseToken,
                    status = success ? "Succeeded"
                    : terminal ? "FailedTerminal"
                    : "Pending",
                    n,
                    next,
                    success,
                    code,
                    error,
                },
                tx,
                cancellationToken: ct
            )
        );
        if (changed == 0)
        {
            await tx.RollbackAsync(ct);
            return;
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO delivery_attempts VALUES(gen_random_uuid(),@id,@n,now(),@code,@error,@ms)",
                new
                {
                    id = i.Id,
                    n,
                    code,
                    error,
                    ms,
                },
                tx,
                cancellationToken: ct
            )
        );
        if (success || terminal)
            await c.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM delivery_outbox WHERE delivery_id=@id",
                    new { id = i.Id },
                    tx,
                    cancellationToken: ct
                )
            );
        else
            await c.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE delivery_outbox SET available_at=@next WHERE delivery_id=@id",
                    new { id = i.Id, next },
                    tx,
                    cancellationToken: ct
                )
            );
        await tx.CommitAsync(ct);
    }

    const string DeliverySql =
        "SELECT id,event_id EventId,endpoint_id EndpointId,status,attempt_count AttemptCount,max_attempts MaxAttempts,next_attempt_at NextAttemptAt,completed_at CompletedAt,last_status_code LastStatusCode,last_error LastError,created_at CreatedAt,updated_at UpdatedAt FROM deliveries";

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) =>
            value switch
            {
                DateTime timestamp => new DateTimeOffset(
                    DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
                ),
                DateTimeOffset timestamp => timestamp,
                _ => throw new DataException(
                    $"Cannot map {value.GetType().Name} to DateTimeOffset."
                ),
            };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
            parameter.Value = value;
    }

    private sealed class DeliveryStatusHandler : SqlMapper.TypeHandler<DeliveryStatus>
    {
        public override DeliveryStatus Parse(object value) =>
            Enum.Parse<DeliveryStatus>((string)value, ignoreCase: false);

        public override void SetValue(IDbDataParameter parameter, DeliveryStatus value) =>
            parameter.Value = value.ToString();
    }
}
