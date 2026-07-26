using System.Diagnostics;
using Microsoft.Extensions.Options;
using ReliableWebhook.Application;
using ReliableWebhook.Domain;

namespace ReliableWebhook.Worker;

public sealed class DeliveryOptions
{
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 10;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int LeaseSeconds { get; set; } = 60;
    public int InitialRetrySeconds { get; set; } = 5;
    public int MaximumRetrySeconds { get; set; } = 3600;
}

public sealed class DeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<DeliveryOptions> options,
    ILogger<DeliveryWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IWebhookStore>();
                var claimedDeliveries = await store.ClaimAsync(
                    settings.BatchSize,
                    TimeSpan.FromSeconds(settings.LeaseSeconds),
                    settings.MaxAttempts,
                    stoppingToken
                );
                foreach (var delivery in claimedDeliveries)
                    await SendAsync(delivery, store, settings, stoppingToken);
                if (claimedDeliveries.Count == 0)
                    await Task.Delay(settings.PollIntervalMilliseconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Delivery polling failed");
                await Task.Delay(settings.PollIntervalMilliseconds, stoppingToken);
            }
        }
    }

    async Task SendAsync(
        ClaimedDelivery delivery,
        IWebhookStore store,
        DeliveryOptions settings,
        CancellationToken stoppingToken
    )
    {
        var attemptNumber = delivery.AttemptCount + 1;
        var stopwatch = Stopwatch.StartNew();
        int? statusCode = null;
        string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, delivery.DestinationUrl)
            {
                Content = new StringContent(
                    delivery.Payload,
                    System.Text.Encoding.UTF8,
                    "application/json"
                ),
            };
            request.Headers.Add("X-Webhook-Event", delivery.EventType);
            request.Headers.Add("X-Webhook-Delivery", delivery.Id.ToString());
            request.Headers.Add(
                "X-Webhook-Signature",
                "sha256=" + WebhookSigner.Sign(delivery.Secret, delivery.Payload)
            );
            using var response = await httpClientFactory
                .CreateClient("webhooks")
                .SendAsync(request, timeout.Token);
            statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                await store.CompleteAsync(
                    delivery,
                    attemptNumber,
                    statusCode.Value,
                    (int)stopwatch.ElapsedMilliseconds,
                    stoppingToken
                );
                return;
            }
            error = $"HTTP {statusCode}";
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            error = "Request timed out";
        }
        catch (HttpRequestException ex)
        {
            error = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
        }
        var terminal = attemptNumber >= delivery.MaxAttempts;
        var next =
            DateTimeOffset.UtcNow
            + RetryPolicy.Delay(
                attemptNumber,
                TimeSpan.FromSeconds(settings.InitialRetrySeconds),
                TimeSpan.FromSeconds(settings.MaximumRetrySeconds)
            );
        await store.FailAsync(
            delivery,
            attemptNumber,
            statusCode,
            error ?? "Delivery failed",
            (int)stopwatch.ElapsedMilliseconds,
            next,
            terminal,
            stoppingToken
        );
    }
}
