using System.Net;
using System.Net.Http.Json;
using NovaWallet.Api;

namespace NovaWallet.IntegrationTests;

public class AuthEndpointsTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
{
    [Fact]
    public async Task IssueToken_ForTheReservedSettlementCustomerId_ReturnsValidationProblem()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/token", new TokenRequest("SYSTEM_SETTLEMENT"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IssueToken_ForAnOrdinaryCustomerId_Succeeds()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/token", new TokenRequest($"customer-{Guid.NewGuid()}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
