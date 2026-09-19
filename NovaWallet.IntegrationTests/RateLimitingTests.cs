using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api;

namespace NovaWallet.IntegrationTests;

public class RateLimitingTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
{
    private WebApplicationFactory<Program> WithLimit(int permitLimit, int windowSeconds = 60)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new RateLimitOptions { PermitLimit = permitLimit, WindowSeconds = windowSeconds });
            });
        });
    }

    private static async Task<(HttpClient Client, Guid WalletId, string AccountNumber)> CreateFundedWalletAsync(HttpClient client, long initialBalanceKobo)
    {
        var customerId = $"customer-{Guid.NewGuid()}";
        var tokenResponse = await client.PostAsJsonAsync("/auth/token", new TokenRequest(customerId));
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);

        var createResponse = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var wallet = await createResponse.Content.ReadFromJsonAsync<WalletResponse>();

        if (initialBalanceKobo > 0)
        {
            await client.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(wallet!.AccountNumber, initialBalanceKobo));
        }

        return (client, wallet!.WalletId, wallet.AccountNumber);
    }

    private static Task<HttpResponseMessage> PostTransferAsync(HttpClient client, Guid fromWalletId, string toAccountNumber, long amountKobo)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/transactions/internal-transfers")
        {
            Content = JsonContent.Create(new TransferRequest(fromWalletId, toAccountNumber, amountKobo))
        };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(message);
    }

    [Fact]
    public async Task Transfer_BurstBeyondLimit_RejectedWithRateLimitProblemDetails()
    {
        const int permitLimit = 3;
        const int attempts = 6;

        var app = WithLimit(permitLimit);
        var (fromClient, fromWalletId, _) = await CreateFundedWalletAsync(app.CreateClient(), 1_000_000);
        var (_, _, toAccountNumber) = await CreateFundedWalletAsync(app.CreateClient(), 0);

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < attempts; i++)
        {
            responses.Add(await PostTransferAsync(fromClient, fromWalletId, toAccountNumber, 1_000));
        }

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();

        Assert.Equal(permitLimit, succeeded);
        Assert.Equal(attempts - permitLimit, rejected.Count);

        var rejectedBody = await rejected[0].Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("RATE_LIMIT_EXCEEDED", rejectedBody.GetProperty("errorCode").GetString());
        Assert.Equal("application/problem+json", rejected[0].Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Transfer_RateLimitScopedToTransfersEndpoint_OtherEndpointsUnaffected()
    {
        const int permitLimit = 2;

        var app = WithLimit(permitLimit);
        var (fromClient, fromWalletId, fromAccountNumber) = await CreateFundedWalletAsync(app.CreateClient(), 1_000_000);
        var (_, _, toAccountNumber) = await CreateFundedWalletAsync(app.CreateClient(), 0);

        for (var i = 0; i < permitLimit; i++)
        {
            await PostTransferAsync(fromClient, fromWalletId, toAccountNumber, 1_000);
        }

        var balanceResponse = await fromClient.GetAsync($"/wallets/{fromWalletId}");
        Assert.Equal(HttpStatusCode.OK, balanceResponse.StatusCode);

        var creditResponse = await fromClient.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(fromAccountNumber, 1_000));
        Assert.Equal(HttpStatusCode.Created, creditResponse.StatusCode);
    }
}
