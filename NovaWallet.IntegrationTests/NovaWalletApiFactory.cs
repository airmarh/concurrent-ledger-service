using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NovaWallet.Api;
using NovaWallet.Infrastructure;
using Testcontainers.PostgreSql;

namespace NovaWallet.IntegrationTests;

public class NovaWalletApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("novawallet")
        .WithUsername("novawallet")
        .WithPassword("novawallet_test_password")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var connectionString = _postgres.GetConnectionString();

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<NovaWalletDbContext>>();
            services.AddDbContext<NovaWalletDbContext>(options =>
                options.UseNpgsql(connectionString));
            services.AddSingleton(new OutboxOptions { PollIntervalSeconds = 3600 });
        });
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
    }

    public Task StopDatabaseAsync() => _postgres.StopAsync();

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }
}
