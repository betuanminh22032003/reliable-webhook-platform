namespace ReliableWebhook.Domain;

public enum DeliveryStatus { Pending, Processing, Succeeded, FailedTerminal }
public sealed record WebhookEndpoint(Guid Id, string DestinationUrl, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record WebhookEndpointSecret(Guid Id, string DestinationUrl, string Secret, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record WebhookEvent(Guid Id, string EventType, string Payload, string? IdempotencyKey, string PayloadHash, DateTimeOffset CreatedAt);
public sealed record Delivery(Guid Id, Guid EventId, Guid EndpointId, DeliveryStatus Status, int AttemptCount, int MaxAttempts, DateTimeOffset NextAttemptAt, DateTimeOffset? CompletedAt, int? LastStatusCode, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record DeliveryAttempt(Guid Id, Guid DeliveryId, int AttemptNumber, DateTimeOffset AttemptedAt, int? StatusCode, string? Error, int DurationMs);
public sealed record ClaimedDelivery(Guid Id, string DestinationUrl, string Secret, string EventType, string Payload, int AttemptCount, int MaxAttempts, Guid LeaseToken);
