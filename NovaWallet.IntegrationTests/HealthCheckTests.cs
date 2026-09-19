using System.Net;

namespace NovaWallet.IntegrationTests;

public class HealthCheckTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
{
    [Fact]
    public async Task Live_ReturnsHealthy_WithNoAuthRequired()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_ReturnsHealthy_WhenPostgresIsReachable()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

// A dedicated factory/container instance, since this test deliberately kills Postgres
// and must never share state with the happy-path assertions above.
public class HealthCheckDatabaseUnavailableTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
{
    [Fact]
    public async Task Ready_ReturnsUnhealthy_AndLiveStillReturnsHealthy_WhenPostgresIsUnreachable()
    {
        var client = factory.CreateClient();

        await factory.StopDatabaseAsync();

        var readyResponse = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyResponse.StatusCode);

        // Liveness must never depend on a downstream dependency — a DB outage should
        // never get a healthy process killed by the orchestrator.
        var liveResponse = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
    }
}
