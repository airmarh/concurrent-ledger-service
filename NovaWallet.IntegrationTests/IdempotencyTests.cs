using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NovaWallet.Api;

namespace NovaWallet.IntegrationTests;

public class IdempotencyTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
{
    private async Task<(HttpClient Client, Guid WalletId, string AccountNumber)> CreateFundedWalletAsync(long initialBalanceKobo)
    {
        var client = factory.CreateClient();
        var customerId = $"customer-{Guid.NewGuid()}";

        var tokenResponse = await client.PostAsJsonAsync("/auth/token", new TokenRequest(customerId));
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);

        var createResponse = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var wallet = await createResponse.Content.ReadFromJsonAsync<WalletResponse>();

        await client.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(wallet!.AccountNumber, initialBalanceKobo));

        return (client, wallet.WalletId, wallet.AccountNumber);
    }

    [Fact]
    public async Task ReplayingSameKeyAndPayload_ReturnsOriginalResultWithoutReprocessing()
    {
        var (fromClient, fromWalletId, _) = await CreateFundedWalletAsync(50_000);
        var (_, _, toAccountNumber) = await CreateFundedWalletAsync(0);
        var idempotencyKey = Guid.NewGuid().ToString();
        var request = new TransferRequest(fromWalletId, toAccountNumber, 10_000);

        var first = await InternalTransfersEndpointsTests.PostTransferAsync(fromClient, request, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var firstResult = await first.Content.ReadFromJsonAsync<TransferResponse>();

        var replay = await InternalTransfersEndpointsTests.PostTransferAsync(fromClient, request, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var replayBody = await replay.Content.ReadAsStringAsync();
        var replayResult = await replay.Content.ReadFromJsonAsync<TransferResponse>();

        // Byte-for-byte, not just value-equal: a replay must be indistinguishable from the
        // original response, including field casing, not just carry the same underlying data.
        Assert.Equal(firstBody, replayBody);
        Assert.Equal(firstResult!.DebitTransactionId, replayResult!.DebitTransactionId);

        var balanceResponse = await fromClient.GetAsync($"/wallets/{fromWalletId}");
        var wallet = await balanceResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(40_000, wallet!.BalanceKobo);
    }

    [Fact]
    public async Task ReusingSameKeyWithDifferentPayload_ReturnsConflict()
    {
        var (fromClient, fromWalletId, _) = await CreateFundedWalletAsync(50_000);
        var (_, _, toAccountNumber) = await CreateFundedWalletAsync(0);
        var idempotencyKey = Guid.NewGuid().ToString();

        var first = await InternalTransfersEndpointsTests.PostTransferAsync(
            fromClient, new TransferRequest(fromWalletId, toAccountNumber, 10_000), idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await InternalTransfersEndpointsTests.PostTransferAsync(
            fromClient, new TransferRequest(fromWalletId, toAccountNumber, 20_000), idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var balanceResponse = await fromClient.GetAsync($"/wallets/{fromWalletId}");
        var wallet = await balanceResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(40_000, wallet!.BalanceKobo);
    }

    [Fact]
    public async Task FailedTransfer_ReleasesKeyForRetry()
    {
        var (fromClient, fromWalletId, fromAccountNumber) = await CreateFundedWalletAsync(1_000);
        var (_, _, toAccountNumber) = await CreateFundedWalletAsync(0);
        var idempotencyKey = Guid.NewGuid().ToString();

        var failed = await InternalTransfersEndpointsTests.PostTransferAsync(
            fromClient, new TransferRequest(fromWalletId, toAccountNumber, 2_000), idempotencyKey);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);

        await fromClient.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(fromAccountNumber, 5_000));

        var retried = await InternalTransfersEndpointsTests.PostTransferAsync(
            fromClient, new TransferRequest(fromWalletId, toAccountNumber, 2_000), idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
    }

    [Fact]
    public async Task ConcurrentReplaysOfSameKey_ProcessExactlyOnce()
    {
        var (fromClient, fromWalletId, _) = await CreateFundedWalletAsync(50_000);
        var (_, _, toAccountNumber) = await CreateFundedWalletAsync(0);
        var idempotencyKey = Guid.NewGuid().ToString();
        var request = new TransferRequest(fromWalletId, toAccountNumber, 10_000);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => InternalTransfersEndpointsTests.PostTransferAsync(fromClient, request, idempotencyKey))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        // Timing decides success vs. "still processing," but exactly one transfer must
        // ever happen — the balance assertion below is what actually proves that.
        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"Unexpected status {r.StatusCode}"));

        var balanceResponse = await fromClient.GetAsync($"/wallets/{fromWalletId}");
        var wallet = await balanceResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(40_000, wallet!.BalanceKobo);
    }
}
