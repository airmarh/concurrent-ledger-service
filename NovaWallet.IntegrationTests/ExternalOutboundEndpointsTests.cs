using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;

namespace NovaWallet.IntegrationTests;

public class ExternalOutboundEndpointsTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
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

        if (initialBalanceKobo > 0)
        {
            await client.PostAsJsonAsync(
                "/transactions/external-inbound", new CreditWalletRequest(wallet!.AccountNumber, initialBalanceKobo));
        }

        return (client, wallet!.WalletId, wallet.AccountNumber);
    }

    internal static Task<HttpResponseMessage> PostExternalOutboundDebitAsync(
        HttpClient client, ExternalOutboundDebitRequest request, string? idempotencyKey = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/transactions/external-outbound")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        return client.SendAsync(message);
    }

    [Fact]
    public async Task Debit_DecreasesBalance_AndWritesMatchingCreditLegOnSettlementAccount()
    {
        var (client, walletId, _) = await CreateFundedWalletAsync(100_000);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        var settlementBalanceBefore = await db.Wallets
            .AsNoTracking()
            .Where(w => w.WalletId == WellKnownWalletIds.NgnOutboundSettlementAccount)
            .Select(w => w.BalanceKobo)
            .SingleAsync();

        var response = await PostExternalOutboundDebitAsync(client, new ExternalOutboundDebitRequest(walletId, 30_000));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var debit = await response.Content.ReadFromJsonAsync<ExternalOutboundDebitResponse>();
        Assert.Equal(70_000, debit!.BalanceKobo);

        var settlementWallet = await db.Wallets
            .AsNoTracking()
            .SingleAsync(w => w.WalletId == WellKnownWalletIds.NgnOutboundSettlementAccount);
        Assert.Equal(settlementBalanceBefore + 30_000, settlementWallet.BalanceKobo);

        var debitTxn = await db.Transactions.SingleAsync(t => t.WalletId == walletId && t.Direction == TransactionDirection.Debit);
        Assert.Equal(30_000, debitTxn.AmountKobo);
        Assert.Equal(WellKnownWalletIds.NgnOutboundSettlementAccount, debitTxn.CounterpartyWalletId);

        var debitAudit = await db.AuditLog.SingleAsync(a => a.WalletId == walletId && a.Actor == "EXTERNAL_OUTBOUND");
        Assert.Equal(100_000, debitAudit.BalanceBeforeKobo);
        Assert.Equal(70_000, debitAudit.BalanceAfterKobo);
    }

    [Fact]
    public async Task Debit_WithoutIdempotencyKey_ReturnsValidationProblem()
    {
        var (client, walletId, _) = await CreateFundedWalletAsync(10_000);

        var response = await client.PostAsJsonAsync("/transactions/external-outbound", new ExternalOutboundDebitRequest(walletId, 1_000));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Debit_WithInsufficientBalance_RejectsAndLeavesBalanceUnchanged()
    {
        var (client, walletId, _) = await CreateFundedWalletAsync(1_000);

        var response = await PostExternalOutboundDebitAsync(client, new ExternalOutboundDebitRequest(walletId, 2_000));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var balanceResponse = await client.GetAsync($"/wallets/{walletId}");
        var wallet = await balanceResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(1_000, wallet!.BalanceKobo);
    }

    [Fact]
    public async Task Debit_WalletNotOwnedByCaller_ReturnsForbidden()
    {
        var (_, walletId, _) = await CreateFundedWalletAsync(10_000);
        var (otherClient, _, _) = await CreateFundedWalletAsync(0);

        var response = await PostExternalOutboundDebitAsync(otherClient, new ExternalOutboundDebitRequest(walletId, 1_000));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Debit_ConcurrentRequestsExceedingBalance_NeverOverspendsOrGoesNegative()
    {
        const long amountPerDebit = 10_000;
        const int fundedDebits = 5;
        const int attemptedDebits = 20;

        var (client, walletId, _) = await CreateFundedWalletAsync(amountPerDebit * fundedDebits);

        var tasks = Enumerable.Range(0, attemptedDebits)
            .Select(_ => PostExternalOutboundDebitAsync(client, new ExternalOutboundDebitRequest(walletId, amountPerDebit)))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);

        Assert.Equal(fundedDebits, succeeded);
        Assert.Equal(attemptedDebits - fundedDebits, rejected);

        var balanceResponse = await client.GetAsync($"/wallets/{walletId}");
        var wallet = await balanceResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(0, wallet!.BalanceKobo);
        Assert.True(wallet.BalanceKobo >= 0, "Balance must never go negative.");
    }

    [Fact]
    public async Task Reversal_CreditsBackCustomer_AndDebitsBackSettlement_AndWritesReversalRow()
    {
        var (client, walletId, _) = await CreateFundedWalletAsync(100_000);

        var debitResponse = await PostExternalOutboundDebitAsync(client, new ExternalOutboundDebitRequest(walletId, 30_000));
        var debit = await debitResponse.Content.ReadFromJsonAsync<ExternalOutboundDebitResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        var settlementBalanceAfterDebit = await db.Wallets
            .AsNoTracking()
            .Where(w => w.WalletId == WellKnownWalletIds.NgnOutboundSettlementAccount)
            .Select(w => w.BalanceKobo)
            .SingleAsync();

        var reversalResponse = await client.PostAsJsonAsync(
            $"/transactions/external-outbound/{debit!.TransactionId}/reversal",
            new ReverseExternalOutboundDebitRequest("ACCOUNT_NOT_FOUND"));
        Assert.Equal(HttpStatusCode.Created, reversalResponse.StatusCode);

        var reversal = await reversalResponse.Content.ReadFromJsonAsync<ExternalOutboundReversalResponse>();
        Assert.Equal(100_000, reversal!.BalanceKobo);
        Assert.Equal(debit.TransactionId, reversal.OriginalTransactionId);

        var walletResponse = await client.GetAsync($"/wallets/{walletId}");
        var wallet = await walletResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(100_000, wallet!.BalanceKobo);

        var settlementWallet = await db.Wallets
            .AsNoTracking()
            .SingleAsync(w => w.WalletId == WellKnownWalletIds.NgnOutboundSettlementAccount);
        Assert.Equal(settlementBalanceAfterDebit - 30_000, settlementWallet.BalanceKobo);

        var reversalRow = await db.TransactionReversals.SingleAsync(r => r.OriginalTransactionId == debit.TransactionId);
        Assert.Equal(reversal.ReversalTransactionId, reversalRow.ReversalTransactionId);
        Assert.Equal("ACCOUNT_NOT_FOUND", reversalRow.Reason);

        var reversalAudit = await db.AuditLog.SingleAsync(a => a.WalletId == walletId && a.Actor == "EXTERNAL_OUTBOUND_REVERSAL");
        Assert.Equal(70_000, reversalAudit.BalanceBeforeKobo);
        Assert.Equal(100_000, reversalAudit.BalanceAfterKobo);
    }

    [Fact]
    public async Task Reversal_UnknownTransactionId_ReturnsNotFound()
    {
        var (client, _, _) = await CreateFundedWalletAsync(10_000);

        var response = await client.PostAsJsonAsync(
            $"/transactions/external-outbound/{Guid.NewGuid()}/reversal",
            new ReverseExternalOutboundDebitRequest("ACCOUNT_NOT_FOUND"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reversal_OfAnOrdinaryInboundCredit_ReturnsUnprocessableEntity()
    {
        // An inbound credit's leg is a Credit, not a Debit against the settlement account,
        // so it must be rejected — there's no separate "transaction type" column to check instead.
        var (client, _, accountNumber) = await CreateFundedWalletAsync(0);
        var creditResponse = await client.PostAsJsonAsync(
            "/transactions/external-inbound", new CreditWalletRequest(accountNumber, 10_000));
        var credit = await creditResponse.Content.ReadFromJsonAsync<CreditResponse>();

        var response = await client.PostAsJsonAsync(
            $"/transactions/external-outbound/{credit!.TransactionId}/reversal",
            new ReverseExternalOutboundDebitRequest("ACCOUNT_NOT_FOUND"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Reversal_CalledTwice_SecondCallReturnsConflict()
    {
        var (client, walletId, _) = await CreateFundedWalletAsync(50_000);
        var debitResponse = await PostExternalOutboundDebitAsync(client, new ExternalOutboundDebitRequest(walletId, 10_000));
        var debit = await debitResponse.Content.ReadFromJsonAsync<ExternalOutboundDebitResponse>();

        var firstReversal = await client.PostAsJsonAsync(
            $"/transactions/external-outbound/{debit!.TransactionId}/reversal",
            new ReverseExternalOutboundDebitRequest("ACCOUNT_NOT_FOUND"));
        Assert.Equal(HttpStatusCode.Created, firstReversal.StatusCode);

        var secondReversal = await client.PostAsJsonAsync(
            $"/transactions/external-outbound/{debit.TransactionId}/reversal",
            new ReverseExternalOutboundDebitRequest("ACCOUNT_NOT_FOUND"));
        Assert.Equal(HttpStatusCode.Conflict, secondReversal.StatusCode);

        var walletResponse = await client.GetAsync($"/wallets/{walletId}");
        var wallet = await walletResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(50_000, wallet!.BalanceKobo);
    }

    [Fact]
    public async Task Reversal_ConcurrentDoubleReversalOfSameTransaction_OnlyOneSucceeds()
    {
        // The most important test in this file: a double reversal is exactly the
        // double-spend risk this feature introduces, and must be prevented by the
        // transaction_reversals primary key, not by hoping requests never race.
        const int attempts = 10;

        var (client, walletId, _) = await CreateFundedWalletAsync(50_000);
        var debitResponse = await PostExternalOutboundDebitAsync(client, new ExternalOutboundDebitRequest(walletId, 10_000));
        var debit = await debitResponse.Content.ReadFromJsonAsync<ExternalOutboundDebitResponse>();

        var tasks = Enumerable.Range(0, attempts)
            .Select(_ => client.PostAsJsonAsync(
                $"/transactions/external-outbound/{debit!.TransactionId}/reversal",
                new ReverseExternalOutboundDebitRequest("ACCOUNT_NOT_FOUND")))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var conflicted = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, succeeded);
        Assert.Equal(attempts - 1, conflicted);

        var walletResponse = await client.GetAsync($"/wallets/{walletId}");
        var wallet = await walletResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(50_000, wallet!.BalanceKobo);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        var reversalCount = await db.TransactionReversals.CountAsync(r => r.OriginalTransactionId == debit!.TransactionId);
        Assert.Equal(1, reversalCount);
    }
}
