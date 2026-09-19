using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;

namespace NovaWallet.IntegrationTests;

public class OutboxTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
{
    private async Task<(HttpClient FromClient, Guid FromWalletId, HttpClient ToClient, Guid ToWalletId, string ToAccountNumber)> CreateFundedWalletPairAsync()
    {
        var fromClient = factory.CreateClient();
        var fromTokenResponse = await fromClient.PostAsJsonAsync("/auth/token", new TokenRequest($"customer-{Guid.NewGuid()}"));
        var fromToken = await fromTokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        fromClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fromToken!.AccessToken);
        var fromWalletResponse = await fromClient.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var fromWallet = await fromWalletResponse.Content.ReadFromJsonAsync<WalletResponse>();
        await fromClient.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(fromWallet!.AccountNumber, 100_000));

        var toClient = factory.CreateClient();
        var toTokenResponse = await toClient.PostAsJsonAsync("/auth/token", new TokenRequest($"customer-{Guid.NewGuid()}"));
        var toToken = await toTokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        toClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", toToken!.AccessToken);
        var toWalletResponse = await toClient.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var toWallet = await toWalletResponse.Content.ReadFromJsonAsync<WalletResponse>();

        return (fromClient, fromWallet.WalletId, toClient, toWallet!.WalletId, toWallet.AccountNumber);
    }

    private static Task<HttpResponseMessage> PostTransferAsync(HttpClient client, Guid fromWalletId, string toAccountNumber, long amountKobo, string? correlationId = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/transactions/internal-transfers")
        {
            Content = JsonContent.Create(new TransferRequest(fromWalletId, toAccountNumber, amountKobo))
        };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (correlationId is not null)
            message.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);
        return client.SendAsync(message);
    }

    [Fact]
    public async Task Transfer_ProducesExactlyOneUndispatchedOutboxRow_WithCorrelationIdInPayload()
    {
        var (fromClient, fromWalletId, _, toWalletId, toAccountNumber) = await CreateFundedWalletPairAsync();
        var correlationId = $"outbox-{Guid.NewGuid()}";

        var response = await PostTransferAsync(fromClient, fromWalletId, toAccountNumber, 10_000, correlationId);
        var transfer = await response.Content.ReadFromJsonAsync<TransferResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

        var outboxEvent = await db.Outbox.SingleAsync(e =>
            e.EventType == OutboxEventTypes.TransferCompleted && e.PayloadJson.Contains(transfer!.DebitTransactionId.ToString()));

        Assert.Null(outboxEvent.DispatchedAt);

        var payload = System.Text.Json.JsonSerializer.Deserialize<TransferCompletedPayload>(
            outboxEvent.PayloadJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(correlationId, payload!.CorrelationId);
        Assert.Equal(fromWalletId, payload.FromWalletId);
        Assert.Equal(toWalletId, payload.ToWalletId);
        Assert.Equal(10_000, payload.AmountKobo);
    }

    [Fact]
    public async Task NextPoll_DispatchesEventCommittedByAnEarlierTransfer_EvenThoughNoDispatcherRanAtCommitTime()
    {
        // The transfer (and its outbox row) commits independently of whether anything
        // ever polls it — simulating the dispatcher crashing right after that commit.
        var (fromClient, fromWalletId, _, toWalletId, toAccountNumber) = await CreateFundedWalletPairAsync();
        var response = await PostTransferAsync(fromClient, fromWalletId, toAccountNumber, 5_000);
        var transfer = await response.Content.ReadFromJsonAsync<TransferResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        var beforePoll = await db.Outbox.SingleAsync(e => e.PayloadJson.Contains(transfer!.DebitTransactionId.ToString()));
        Assert.Null(beforePoll.DispatchedAt);

        // Simulates "the next poll" — the dispatcher recovering the event with nothing
        // but what's durably in outbox_events, no in-memory state from the transfer itself.
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        var dispatchedCount = await dispatcher.DispatchPendingAsync();

        Assert.True(dispatchedCount >= 1);

        db.ChangeTracker.Clear();
        var afterPoll = await db.Outbox.SingleAsync(e => e.Id == beforePoll.Id);
        Assert.NotNull(afterPoll.DispatchedAt);
    }

    [Fact]
    public async Task SecondPoll_DoesNotRedispatchAnAlreadyDispatchedEvent()
    {
        var (fromClient, fromWalletId, _, toWalletId, toAccountNumber) = await CreateFundedWalletPairAsync();
        var response = await PostTransferAsync(fromClient, fromWalletId, toAccountNumber, 2_000);
        var transfer = await response.Content.ReadFromJsonAsync<TransferResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();

        await dispatcher.DispatchPendingAsync();

        db.ChangeTracker.Clear();
        var afterFirstPoll = await db.Outbox.SingleAsync(e => e.PayloadJson.Contains(transfer!.DebitTransactionId.ToString()));
        var dispatchedAtAfterFirstPoll = afterFirstPoll.DispatchedAt;
        Assert.NotNull(dispatchedAtAfterFirstPoll);

        var secondPollDispatchedCount = await dispatcher.DispatchPendingAsync();

        db.ChangeTracker.Clear();
        var afterSecondPoll = await db.Outbox.SingleAsync(e => e.Id == afterFirstPoll.Id);

        Assert.Equal(dispatchedAtAfterFirstPoll, afterSecondPoll.DispatchedAt);
        // Only asserts this specific row wasn't touched again; other tests' rows may
        // still be pending and legitimately get picked up by this same call.
        Assert.True(secondPollDispatchedCount >= 0);
    }

    [Fact]
    public async Task PollBatch_OneUnpublishableEvent_DoesNotBlockOtherEventsInTheSameBatch()
    {
        var (fromClient, fromWalletId, _, toWalletId, toAccountNumber) = await CreateFundedWalletPairAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

        var brokenEvent = OutboxEvent.Create(OutboxEventTypes.TransferCompleted, "{ not valid json", DateTime.UtcNow);
        db.Outbox.Add(brokenEvent);
        await db.SaveChangesAsync();

        var response = await PostTransferAsync(fromClient, fromWalletId, toAccountNumber, 1_000);
        var transfer = await response.Content.ReadFromJsonAsync<TransferResponse>();

        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        await dispatcher.DispatchPendingAsync();

        db.ChangeTracker.Clear();

        var afterBrokenEvent = await db.Outbox.SingleAsync(e => e.Id == brokenEvent.Id);
        Assert.Null(afterBrokenEvent.DispatchedAt);
        Assert.Equal(1, afterBrokenEvent.AttemptCount);
        Assert.NotNull(afterBrokenEvent.NextAttemptAt);

        var legitimateEvent = await db.Outbox.SingleAsync(e => e.PayloadJson.Contains(transfer!.DebitTransactionId.ToString()));
        Assert.NotNull(legitimateEvent.DispatchedAt);
    }

    [Fact]
    public async Task ExternalInboundCredit_ProducesExactlyOneUndispatchedOutboxRow()
    {
        var client = factory.CreateClient();
        var tokenResponse = await client.PostAsJsonAsync("/auth/token", new TokenRequest($"customer-{Guid.NewGuid()}"));
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        var walletResponse = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var wallet = await walletResponse.Content.ReadFromJsonAsync<WalletResponse>();

        var creditResponse = await client.PostAsJsonAsync(
            "/transactions/external-inbound", new CreditWalletRequest(wallet!.AccountNumber, 10_000));
        var credit = await creditResponse.Content.ReadFromJsonAsync<CreditResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

        var outboxEvent = await db.Outbox.SingleAsync(e =>
            e.EventType == OutboxEventTypes.ExternalInboundCreditCompleted && e.PayloadJson.Contains(credit!.TransactionId.ToString()));
        Assert.Null(outboxEvent.DispatchedAt);

        var payload = System.Text.Json.JsonSerializer.Deserialize<ExternalInboundCreditCompletedPayload>(
            outboxEvent.PayloadJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(wallet.WalletId, payload!.WalletId);
        Assert.Equal(10_000, payload.AmountKobo);
    }

    [Fact]
    public async Task ExternalOutboundDebitAndReversal_EachProduceExactlyOneUndispatchedOutboxRow()
    {
        var client = factory.CreateClient();
        var tokenResponse = await client.PostAsJsonAsync("/auth/token", new TokenRequest($"customer-{Guid.NewGuid()}"));
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        var walletResponse = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var wallet = await walletResponse.Content.ReadFromJsonAsync<WalletResponse>();
        await client.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(wallet!.AccountNumber, 50_000));

        var debitMessage = new HttpRequestMessage(HttpMethod.Post, "/transactions/external-outbound")
        {
            Content = JsonContent.Create(new ExternalOutboundDebitRequest(wallet.WalletId, 10_000))
        };
        debitMessage.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var debitResponse = await client.SendAsync(debitMessage);
        var debit = await debitResponse.Content.ReadFromJsonAsync<ExternalOutboundDebitResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

        var debitEvent = await db.Outbox.SingleAsync(e =>
            e.EventType == OutboxEventTypes.ExternalOutboundDebitCompleted && e.PayloadJson.Contains(debit!.TransactionId.ToString()));
        Assert.Null(debitEvent.DispatchedAt);

        var reversalResponse = await client.PostAsJsonAsync(
            $"/transactions/external-outbound/{debit!.TransactionId}/reversal",
            new ReverseExternalOutboundDebitRequest("ACCOUNT_NOT_FOUND"));
        var reversal = await reversalResponse.Content.ReadFromJsonAsync<ExternalOutboundReversalResponse>();

        var reversalEvent = await db.Outbox.SingleAsync(e =>
            e.EventType == OutboxEventTypes.ExternalOutboundReversed && e.PayloadJson.Contains(reversal!.ReversalTransactionId.ToString()));
        Assert.Null(reversalEvent.DispatchedAt);

        var reversalPayload = System.Text.Json.JsonSerializer.Deserialize<ExternalOutboundReversedPayload>(
            reversalEvent.PayloadJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(debit.TransactionId, reversalPayload!.OriginalTransactionId);
        Assert.Equal("ACCOUNT_NOT_FOUND", reversalPayload.Reason);
    }

    [Fact]
    public async Task DispatchPendingAsync_ConcurrentCallsSimulatingMultiplePods_NeverDoubleDispatchesTheSameEvent()
    {
        // Drain anything left un-dispatched by earlier tests in this fixture (they all
        // share one Postgres container) so this test's counts aren't contaminated by
        // rows it didn't seed itself.
        using (var drainScope = factory.Services.CreateScope())
        {
            var drainDispatcher = drainScope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
            while (await drainDispatcher.DispatchPendingAsync() > 0) { }
        }

        const int eventCount = 100;
        const int concurrentDispatchers = 4;

        List<Guid> seededIds;
        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
            var seeded = Enumerable.Range(0, eventCount)
                .Select(i => OutboxEvent.Create(OutboxEventTypes.TransferCompleted, $$"""{"seed":{{i}}}""", DateTime.UtcNow))
                .ToList();
            db.Outbox.AddRange(seeded);
            await db.SaveChangesAsync();
            seededIds = seeded.Select(e => e.Id).ToList();
        }

        // Each call gets its own DI scope — its own DbContext and connection, no shared
        // in-memory state — the same isolation a second (or third, or fourth) pod's
        // OutboxDispatcherHostedService would have. Only Postgres is shared between them.
        var tasks = Enumerable.Range(0, concurrentDispatchers)
            .Select(async _ =>
            {
                using var scope = factory.Services.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
                return await dispatcher.DispatchPendingAsync();
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // If FOR UPDATE SKIP LOCKED weren't actually preventing two "pods" from claiming
        // the same row, that row's event would be counted in more than one call's
        // returned total, and the sum would exceed what was actually seeded.
        Assert.Equal(eventCount, results.Sum());

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        var stillPending = await verifyDb.Outbox.CountAsync(e => seededIds.Contains(e.Id) && e.DispatchedAt == null);
        Assert.Equal(0, stillPending);
    }
}
