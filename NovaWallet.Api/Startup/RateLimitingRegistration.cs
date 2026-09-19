using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace NovaWallet.Api;

public static class RateLimitingRegistration
{
    public static IServiceCollection AddNovaWalletRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.OnRejected = (context, ct) => new ValueTask(ProblemDetailsWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status429TooManyRequests,
                "RATE_LIMIT_EXCEEDED",
                "Too many transfer requests. Please slow down and try again shortly.",
                ct));

            options.AddPolicy("transfers", httpContext =>
            {
                var limits = httpContext.RequestServices.GetRequiredService<RateLimitOptions>();
                var partitionKey = httpContext.User.FindFirst("customer_id")?.Value
                                    ?? httpContext.Connection.RemoteIpAddress?.ToString()
                                    ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                    QueueLimit = 0
                });
            });
        });

        return services;
    }
}
