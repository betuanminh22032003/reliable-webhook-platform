using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReliableWebhook.Domain;

namespace ReliableWebhook.Application;

public static class DestinationUrlValidator
{
    public static bool IsValid(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(uri.Host)
        && uri.UserInfo.Length == 0;
}

public static class WebhookSigner
{
    public static string Sign(string secret, string payload) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))
        );

    public static bool Verify(string secret, string payload, string signature)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(Sign(secret, payload)),
                Convert.FromHexString(signature)
            );
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class RetryPolicy
{
    public static TimeSpan Delay(int attempt, TimeSpan initial, TimeSpan maximum)
    {
        var delay = initial;
        for (var i = 1; i < attempt && delay < maximum; i++)
            delay = TimeSpan.FromTicks(
                Math.Min(
                    maximum.Ticks,
                    delay.Ticks > maximum.Ticks / 2 ? maximum.Ticks : delay.Ticks * 2
                )
            );
        return delay > maximum ? maximum : delay;
    }
}

public static class PayloadHasher
{
    public static string Hash(string eventType, JsonElement payload) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(eventType + "\n" + payload.GetRawText()))
        );
}

public sealed record CreateEndpoint(string DestinationUrl, string Secret, bool Enabled = true);

public sealed record CreatedEndpoint(
    Guid Id,
    string DestinationUrl,
    string Secret,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record PublishEvent(string EventType, JsonElement Payload, string? IdempotencyKey);

public sealed record PublishResult(WebhookEvent Event, bool IsDuplicate);

public interface IWebhookStore
{
    Task<CreatedEndpoint> CreateEndpointAsync(CreateEndpoint request, CancellationToken ct);
    Task<WebhookEndpoint?> GetEndpointAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<WebhookEndpoint>> ListEndpointsAsync(CancellationToken ct);
    Task<bool> DeleteEndpointAsync(Guid id, CancellationToken ct);
    Task<PublishResult> PublishAsync(PublishEvent request, CancellationToken ct);
    Task<WebhookEvent?> GetEventAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Delivery>> GetDeliveriesAsync(Guid eventId, CancellationToken ct);
    Task<(Delivery Delivery, IReadOnlyList<DeliveryAttempt> Attempts)?> GetDeliveryAsync(
        Guid id,
        CancellationToken ct
    );
    Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(
        int count,
        TimeSpan lease,
        int maxAttempts,
        CancellationToken ct
    );
    Task CompleteAsync(
        ClaimedDelivery item,
        int attemptNumber,
        int statusCode,
        int durationMs,
        CancellationToken ct
    );
    Task FailAsync(
        ClaimedDelivery item,
        int attemptNumber,
        int? statusCode,
        string error,
        int durationMs,
        DateTimeOffset nextAttempt,
        bool terminal,
        CancellationToken ct
    );
}
