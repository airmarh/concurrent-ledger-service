using NovaWallet.Domain;
using NovaWallet.Infrastructure;

namespace NovaWallet.Api;

public static class ServiceRegistration
{
    public static IServiceCollection AddNovaWalletServices(this IServiceCollection services)
    {
        services.AddScoped<IWalletRepository, WalletRepository>();
        services.AddScoped<IWalletCreditService, WalletCreditService>();
        services.AddScoped<ITransferService, TransferService>();
        services.AddScoped<IOutboundService, OutboundService>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<ITransactionHistoryRepository, TransactionHistoryRepository>();
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddHostedService<OutboxDispatcherHostedService>();

        return services;
    }
}
