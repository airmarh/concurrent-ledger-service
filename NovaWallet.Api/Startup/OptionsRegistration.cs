using NovaWallet.Infrastructure;

namespace NovaWallet.Api;

public static class OptionsRegistration
{
    public static IServiceCollection AddNovaWalletOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration.GetSection(TransferLimitsOptions.SectionName).Get<TransferLimitsOptions>()
                               ?? new TransferLimitsOptions());
        services.AddSingleton(configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>()
                               ?? new RateLimitOptions());
        services.AddSingleton(configuration.GetSection(OutboxOptions.SectionName).Get<OutboxOptions>()
                               ?? new OutboxOptions());
        services.AddSingleton(configuration.GetSection(OutboxRetryOptions.SectionName).Get<OutboxRetryOptions>()
                               ?? new OutboxRetryOptions());

        return services;
    }
}
