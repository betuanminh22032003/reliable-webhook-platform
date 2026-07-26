using ReliableWebhook.Infrastructure;
using ReliableWebhook.Worker;
var builder = Host.CreateApplicationBuilder(args); builder.Services.AddInfrastructure(builder.Configuration); builder.Services.Configure<DeliveryOptions>(builder.Configuration.GetSection("Delivery")); builder.Services.AddHttpClient("webhooks", c => c.Timeout = Timeout.InfiniteTimeSpan); builder.Services.AddHostedService<DeliveryWorker>(); var host = builder.Build(); await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(); await host.RunAsync();
