using NovaWallet.Domain;

namespace NovaWallet.Api;

public class OutboxDispatcherHostedService(
    IServiceScopeFactory scopeFactory,
    OutboxOptions options,
    ILogger<OutboxDispatcherHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
                var dispatchedCount = await dispatcher.DispatchPendingAsync(stoppingToken);

                if (dispatchedCount > 0)
                    logger.LogInformation("Outbox poll dispatched {Count} events", dispatchedCount);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Outbox dispatch poll failed");
            }
        }
    }
}
