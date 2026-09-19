using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;

namespace NovaWallet.IntegrationTests;

public class InternalTransfersEndpointsTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
{
    private async Task<(HttpClient Client, Guid WalletId, string AccountNumber, string CustomerId)> CreateFundedWalletAsync(long initialBalanceKobo)
    {
        var client = factory.CreateClient();
        var customerId = $"customer-{Guid.NewGuid()}";

        var tokenResponse = await client.PostAsJsonAsync("/auth/token", new TokenRequest(customerId));
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);

        var createResponse = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var wallet = await createResponse.Content.ReadFromJsonAsync<WalletResponse>();

        if (initialBalanceKobo > 0)
        {
            await client.PostAsJsonAsync(
                "/transactions/external-inbound", new CreditWalletRequest(wallet!.AccountNumber, initialBalanceKobo));
        }

        return (client, wallet!.WalletId, wallet.AccountNumber, customerId);
    }

    internal static Task<HttpResponseMessage> PostTransferAsync(HttpClient client, TransferRequest request, string? idempotencyKey = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/transactions/internal-transfers")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        return client.SendAsync(message);
    }

    [Fact]
    public async Task Transfer_WithSufficientBalance_MovesExactAmountAndRecordsBothLegs()
    {
        var (fromClient, fromWalletId, _, _) = await CreateFundedWalletAsync(100_000);
        var (_, toWalletId, toAccountNumber, _) = await CreateFundedWalletAsync(0);

        var response = await PostTransferAsync(fromClient, new TransferRequest(fromWalletId, toAccountNumber, 30_000));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<TransferResponse>();
        Assert.Equal(70_000, result!.FromBalanceKobo);
        Assert.Equal(30_000, result.ToBalanceKobo);

        var fromBalance = await fromClient.GetAsync($"/wallets/{fromWalletId}");
        var fromWallet = await fromBalance.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(70_000, fromWallet!.BalanceKobo);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

        var debitTxn = await db.Transactions.SingleAsync(t => t.WalletId == fromWalletId && t.Direction == Domain.TransactionDirection.Debit);
        Assert.Equal(30_000, debitTxn.AmountKobo);
        Assert.Equal(toWalletId, debitTxn.CounterpartyWalletId);

        var creditTxn = await db.Transactions.SingleAsync(t => t.WalletId == toWalletId && t.Direction == Domain.TransactionDirection.Credit && t.CounterpartyWalletId == fromWalletId);
        Assert.Equal(30_000, creditTxn.AmountKobo);

        var debitAudit = await db.AuditLog.SingleAsync(a => a.WalletId == fromWalletId && a.Actor == "INTERNAL_TRANSFER");
        Assert.Equal(100_000, debitAudit.BalanceBeforeKobo);
        Assert.Equal(70_000, debitAudit.BalanceAfterKobo);
    }

    [Fact]
    public async Task Transfer_WithoutIdempotencyKey_ReturnsValidationProblem()
    {
        var (fromClient, fromWalletId, _, _) = await CreateFundedWalletAsync(10_000);
        var (_, _, toAccountNumber, _) = await CreateFundedWalletAsync(0);

        var response = await fromClient.PostAsJsonAsync("/transactions/internal-transfers", new TransferRequest(fromWalletId, toAccountNumber, 1_000));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_WithInsufficientBalance_RejectsAndLeavesBalancesUnchanged()
    {
        var (fromClient, fromWalletId, _, _) = await CreateFundedWalletAsync(1_000);
        var (_, _, toAccountNumber, _) = await CreateFundedWalletAsync(0);

        var response = await PostTransferAsync(fromClient, new TransferRequest(fromWalletId, toAccountNumber, 2_000));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var fromBalance = await fromClient.GetAsync($"/wallets/{fromWalletId}");
        var fromWallet = await fromBalance.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(1_000, fromWallet!.BalanceKobo);
    }

    [Fact]
    public async Task Transfer_FromWalletNotOwnedByCaller_ReturnsForbidden()
    {
        var (_, fromWalletId, _, _) = await CreateFundedWalletAsync(10_000);
        var (otherClient, _, toAccountNumber, _) = await CreateFundedWalletAsync(0);

        var response = await PostTransferAsync(otherClient, new TransferRequest(fromWalletId, toAccountNumber, 1_000));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_ToSameWallet_ReturnsUnprocessableEntity()
    {
        var (client, walletId, accountNumber, _) = await CreateFundedWalletAsync(10_000);

        var response = await PostTransferAsync(client, new TransferRequest(walletId, accountNumber, 1_000));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData(WellKnownWalletIds.NgnInboundSettlementAccountNumber)]
    [InlineData(WellKnownWalletIds.NgnOutboundSettlementAccountNumber)]
    public async Task Transfer_ToASettlementAccount_ReturnsUnprocessableEntity(string settlementAccountNumber)
    {
        // The destination side needs no ownership check, so any authenticated customer
        // can reach this — TransferService must reject both settlement accounts outright,
        // not rely on either being hard to address.
        var (client, fromWalletId, _, _) = await CreateFundedWalletAsync(10_000);

        var response = await PostTransferAsync(
            client, new TransferRequest(fromWalletId, settlementAccountNumber, 1_000));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_UnknownDestinationAccountNumber_ReturnsNotFound()
    {
        var (client, fromWalletId, _, _) = await CreateFundedWalletAsync(10_000);

        var response = await PostTransferAsync(client, new TransferRequest(fromWalletId, AccountNumberGenerator.Generate(), 1_000));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_ConcurrentRequestsExceedingBalance_NeverOverspendsOrGoesNegative()
    {
        const long amountPerTransfer = 10_000;
        const int fundedTransfers = 5;
        const int attemptedTransfers = 20;

        var (fromClient, fromWalletId, _, _) = await CreateFundedWalletAsync(amountPerTransfer * fundedTransfers);
        var (toClient, toWalletId, toAccountNumber, _) = await CreateFundedWalletAsync(0);

        var tasks = Enumerable.Range(0, attemptedTransfers)
            .Select(_ => PostTransferAsync(fromClient, new TransferRequest(fromWalletId, toAccountNumber, amountPerTransfer)))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);

        Assert.Equal(fundedTransfers, succeeded);
        Assert.Equal(attemptedTransfers - fundedTransfers, rejected);

        var fromBalanceResponse = await fromClient.GetAsync($"/wallets/{fromWalletId}");
        var fromWallet = await fromBalanceResponse.Content.ReadFromJsonAsync<WalletResponse>();
        var toBalanceResponse = await toClient.GetAsync($"/wallets/{toWalletId}");
        var toWallet = await toBalanceResponse.Content.ReadFromJsonAsync<WalletResponse>();

        Assert.Equal(0, fromWallet!.BalanceKobo);
        Assert.True(fromWallet.BalanceKobo >= 0, "Balance must never go negative.");
        Assert.Equal(amountPerTransfer * fundedTransfers, toWallet!.BalanceKobo);
        Assert.Equal(amountPerTransfer * fundedTransfers, fromWallet.BalanceKobo + toWallet.BalanceKobo - 0);
    }

    [Fact]
    public async Task Transfer_ConcurrentOppositeDirectionTransfers_DoNotDeadlock()
    {
        var (clientA, walletA, accountNumberA, _) = await CreateFundedWalletAsync(100_000);
        var (clientB, walletB, accountNumberB, _) = await CreateFundedWalletAsync(100_000);

        var taskAtoB = PostTransferAsync(clientA, new TransferRequest(walletA, accountNumberB, 1_000));
        var taskBtoA = PostTransferAsync(clientB, new TransferRequest(walletB, accountNumberA, 2_000));

        var completed = await Task.WhenAll(taskAtoB, taskBtoA).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.All(completed, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        var balanceA = await (await clientA.GetAsync($"/wallets/{walletA}")).Content.ReadFromJsonAsync<WalletResponse>();
        var balanceB = await (await clientB.GetAsync($"/wallets/{walletB}")).Content.ReadFromJsonAsync<WalletResponse>();

        Assert.Equal(101_000, balanceA!.BalanceKobo);
        Assert.Equal(99_000, balanceB!.BalanceKobo);
    }
}
