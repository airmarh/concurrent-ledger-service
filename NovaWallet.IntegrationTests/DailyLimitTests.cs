using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NovaWallet.Api;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;

namespace NovaWallet.IntegrationTests;

public class DailyLimitTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
{
    private WebApplicationFactory<Program> WithLimit(long dailyLimitKobo, DateTime nowUtc)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IClock>(new FixedClock(nowUtc));
                services.AddSingleton(new TransferLimitsOptions { DailyOutboundLimitKobo = dailyLimitKobo });
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

    private static Task<HttpResponseMessage> PostExternalOutboundDebitAsync(HttpClient client, Guid walletId, long amountKobo)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/transactions/external-outbound")
        {
            Content = JsonContent.Create(new ExternalOutboundDebitRequest(walletId, amountKobo))
        };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(message);
    }

    private static async Task SeedHistoricalDebitAsync(WebApplicationFactory<Program> app, Guid walletId, long amountKobo, DateTime createdAtUtc)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transactions (id, wallet_id, counterparty_wallet_id, amount_kobo, direction, created_at)
            VALUES (@id, @walletId, NULL, @amount, 'Debit', @createdAt)
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("walletId", walletId);
        command.Parameters.AddWithValue("amount", amountKobo);
        command.Parameters.AddWithValue("createdAt", createdAtUtc);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Transfer_ExceedingDailyLimit_RejectedWithNoStateMutated()
    {
        var app = WithLimit(dailyLimitKobo: 15_000, nowUtc: DateTime.UtcNow);
        var client = app.CreateClient();
        var (fromClient, fromWalletId, _) = await CreateFundedWalletAsync(client, 100_000);
        var (_, _, toAccountNumber) = await CreateFundedWalletAsync(app.CreateClient(), 0);

        var first = await PostTransferAsync(fromClient, fromWalletId, toAccountNumber, 10_000);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PostTransferAsync(fromClient, fromWalletId, toAccountNumber, 10_000);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

        var balanceResponse = await fromClient.GetAsync($"/wallets/{fromWalletId}");
        var wallet = await balanceResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(90_000, wallet!.BalanceKobo);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        var debitCount = await db.Transactions.CountAsync(t => t.WalletId == fromWalletId && t.Direction == TransactionDirection.Debit);
        Assert.Equal(1, debitCount);
    }

    [Fact]
    public async Task Transfer_PreviousWatDaySpend_DoesNotCountTowardTodaysLimit()
    {
        // Anchored to the real current WAT boundary, not a hardcoded date — live transfers
        // stamp their own real DateTime.UtcNow regardless of any clock override, so only the
        // fixture's historical seed row is placed deliberately before the boundary.
        var boundaryUtc = WestAfricaTime.GetCurrentDayStartUtc(DateTime.UtcNow);
        var justBeforeBoundary = boundaryUtc.AddSeconds(-1);

        var app = WithLimit(dailyLimitKobo: 15_000, nowUtc: DateTime.UtcNow);
        var client = app.CreateClient();
        var (fromClient, fromWalletId, _) = await CreateFundedWalletAsync(client, 100_000);
        var (_, _, toAccountNumber) = await CreateFundedWalletAsync(app.CreateClient(), 0);

        // Spent right up to the (previous WAT day's) limit before the boundary.
        await SeedHistoricalDebitAsync(app, fromWalletId, 15_000, justBeforeBoundary);

        // Today's spend is 0 so far, so this transfer should succeed even though the wallet
        // already moved 15,000 kobo — just on the previous WAT day.
        var afterBoundaryTransfer = await PostTransferAsync(fromClient, fromWalletId, toAccountNumber, 10_000);
        Assert.Equal(HttpStatusCode.Created, afterBoundaryTransfer.StatusCode);

        // A second transfer today would push today's total to 20,000 > the 15,000 limit.
        var overLimitTransfer = await PostTransferAsync(fromClient, fromWalletId, toAccountNumber, 10_000);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimitTransfer.StatusCode);
    }

    [Fact]
    public async Task Transfer_ConcurrentBurst_NeverCollectivelyExceedsDailyLimit()
    {
        const long amountPerTransfer = 10_000;
        const long dailyLimitKobo = 50_000;
        const int attemptedTransfers = 10;
        const int expectedSucceeding = 5; // 50,000 / 10,000

        var app = WithLimit(dailyLimitKobo, DateTime.UtcNow);
        var client = app.CreateClient();
        var (fromClient, fromWalletId, _) = await CreateFundedWalletAsync(client, amountPerTransfer * attemptedTransfers);
        var (toClient, toWalletId, toAccountNumber) = await CreateFundedWalletAsync(app.CreateClient(), 0);

        var tasks = Enumerable.Range(0, attemptedTransfers)
            .Select(_ => PostTransferAsync(fromClient, fromWalletId, toAccountNumber, amountPerTransfer))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        Assert.Equal(expectedSucceeding, succeeded);
        Assert.Equal(attemptedTransfers - expectedSucceeding, rejected);

        var toBalanceResponse = await toClient.GetAsync($"/wallets/{toWalletId}");
        var toWallet = await toBalanceResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(dailyLimitKobo, toWallet!.BalanceKobo);
    }

    [Fact]
    public async Task ExternalOutboundDebit_ExceedingDailyLimit_RejectedWithNoStateMutated()
    {
        var app = WithLimit(dailyLimitKobo: 15_000, nowUtc: DateTime.UtcNow);
        var client = app.CreateClient();
        var (fromClient, walletId, _) = await CreateFundedWalletAsync(client, 100_000);

        var first = await PostExternalOutboundDebitAsync(fromClient, walletId, 10_000);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PostExternalOutboundDebitAsync(fromClient, walletId, 10_000);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

        var balanceResponse = await fromClient.GetAsync($"/wallets/{walletId}");
        var wallet = await balanceResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(90_000, wallet!.BalanceKobo);
    }

    [Fact]
    public async Task DailyLimit_IsSharedBetweenInternalTransferAndExternalOutboundDebit()
    {
        var app = WithLimit(dailyLimitKobo: 15_000, nowUtc: DateTime.UtcNow);
        var client = app.CreateClient();
        var (fromClient, fromWalletId, _) = await CreateFundedWalletAsync(client, 100_000);
        var (_, _, toAccountNumber) = await CreateFundedWalletAsync(app.CreateClient(), 0);

        var transfer = await PostTransferAsync(fromClient, fromWalletId, toAccountNumber, 10_000);
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        
        var outboundDebit = await PostExternalOutboundDebitAsync(fromClient, fromWalletId, 10_000);
        Assert.Equal(HttpStatusCode.TooManyRequests, outboundDebit.StatusCode);

        var withinRemainingHeadroom = await PostExternalOutboundDebitAsync(fromClient, fromWalletId, 5_000);
        Assert.Equal(HttpStatusCode.Created, withinRemainingHeadroom.StatusCode);
    }
}
