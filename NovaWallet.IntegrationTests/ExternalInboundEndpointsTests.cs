using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NovaWallet.Api;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;

namespace NovaWallet.IntegrationTests;

public class ExternalInboundEndpointsTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
{
    private async Task<(HttpClient Client, Guid WalletId, string AccountNumber)> CreateAuthenticatedClientWithWalletAsync()
    {
        var client = factory.CreateClient();
        var customerId = $"customer-{Guid.NewGuid()}";

        var tokenResponse = await client.PostAsJsonAsync("/auth/token", new TokenRequest(customerId));
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);

        var createResponse = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var wallet = await createResponse.Content.ReadFromJsonAsync<WalletResponse>();

        return (client, wallet!.WalletId, wallet.AccountNumber);
    }

    [Fact]
    public async Task Credit_IncreasesBalance_AndWritesTransactionAndAuditRows()
    {
        var (client, walletId, accountNumber) = await CreateAuthenticatedClientWithWalletAsync();

        var creditResponse = await client.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(accountNumber, 150_000));
        Assert.Equal(HttpStatusCode.Created, creditResponse.StatusCode);

        var credit = await creditResponse.Content.ReadFromJsonAsync<CreditResponse>();
        Assert.Equal(150_000, credit!.BalanceKobo);

        var balanceResponse = await client.GetAsync($"/wallets/{walletId}");
        var wallet = await balanceResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(150_000, wallet!.BalanceKobo);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

        var storedTransaction = await db.Transactions.SingleAsync(t => t.WalletId == walletId);
        Assert.Equal(150_000, storedTransaction.AmountKobo);

        var storedAudit = await db.AuditLog.SingleAsync(a => a.WalletId == walletId);
        Assert.Equal(0, storedAudit.BalanceBeforeKobo);
        Assert.Equal(150_000, storedAudit.BalanceAfterKobo);
        Assert.Equal("EXTERNAL_INBOUND", storedAudit.Actor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public async Task Credit_WithNonPositiveAmount_ReturnsUnprocessableEntity(long amountKobo)
    {
        var (client, _, accountNumber) = await CreateAuthenticatedClientWithWalletAsync();

        var response = await client.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(accountNumber, amountKobo));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Credit_UnknownAccountNumber_ReturnsNotFound()
    {
        var (client, _, _) = await CreateAuthenticatedClientWithWalletAsync();

        var response = await client.PostAsJsonAsync(
            "/transactions/external-inbound", new CreditWalletRequest(AccountNumberGenerator.Generate(), 1_000));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Credit_WritesMatchingDebitLegOnSettlementAccount()
    {
        var (client, walletId, accountNumber) = await CreateAuthenticatedClientWithWalletAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        var settlementBalanceBefore = await db.Wallets
            .Where(w => w.WalletId == WellKnownWalletIds.NgnInboundSettlementAccount)
            .Select(w => w.BalanceKobo)
            .SingleAsync();

        var creditResponse = await client.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(accountNumber, 75_000));
        Assert.Equal(HttpStatusCode.Created, creditResponse.StatusCode);

        var settlementWallet = await db.Wallets
            .AsNoTracking()
            .SingleAsync(w => w.WalletId == WellKnownWalletIds.NgnInboundSettlementAccount);
        Assert.Equal(settlementBalanceBefore - 75_000, settlementWallet.BalanceKobo);

        var settlementTxn = await db.Transactions
            .SingleAsync(t => t.WalletId == WellKnownWalletIds.NgnInboundSettlementAccount
                               && t.CounterpartyWalletId == walletId);
        Assert.Equal(75_000, settlementTxn.AmountKobo);
        Assert.Equal(TransactionDirection.Debit, settlementTxn.Direction);

        var creditTxn = await db.Transactions.SingleAsync(t => t.WalletId == walletId);
        Assert.Equal(WellKnownWalletIds.NgnInboundSettlementAccount, creditTxn.CounterpartyWalletId);
    }

    [Fact]
    public async Task AuditLog_DirectUpdateAttempt_IsRejectedByDatabase()
    {
        var (client, walletId, accountNumber) = await CreateAuthenticatedClientWithWalletAsync();
        await client.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(accountNumber, 1_000));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE audit_log SET balance_after_kobo = 999999999 WHERE wallet_id = @walletId";
        command.Parameters.AddWithValue("walletId", walletId);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("append-only", exception.Message);
    }
}
