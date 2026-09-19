using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api;
using NovaWallet.Infrastructure;

namespace NovaWallet.IntegrationTests;

public class CorrelationIdTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
{
    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        var tokenResponse = await client.PostAsJsonAsync("/auth/token", new TokenRequest($"customer-{Guid.NewGuid()}"));
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    [Fact]
    public async Task Request_WithNoCorrelationIdHeader_GetsOneGeneratedAndEchoedBack()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));

        Assert.True(response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var values));
        var correlationId = Assert.Single(values!);
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
    }

    [Fact]
    public async Task Request_WithClientSuppliedCorrelationId_IsHonoredNotOverwritten()
    {
        var client = await CreateAuthenticatedClientAsync();
        var suppliedCorrelationId = $"client-supplied-{Guid.NewGuid()}";

        var message = new HttpRequestMessage(HttpMethod.Post, "/wallets")
        {
            Content = JsonContent.Create(new CreateWalletRequest(null, null))
        };
        message.Headers.Add(CorrelationIdMiddleware.HeaderName, suppliedCorrelationId);

        var response = await client.SendAsync(message);

        var echoedCorrelationId = Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName));
        Assert.Equal(suppliedCorrelationId, echoedCorrelationId);
    }

    [Fact]
    public async Task Credit_WithClientSuppliedCorrelationId_WritesSameIdToAuditLog()
    {
        var client = await CreateAuthenticatedClientAsync();
        var walletResponse = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var wallet = await walletResponse.Content.ReadFromJsonAsync<WalletResponse>();

        var suppliedCorrelationId = $"credit-correlation-{Guid.NewGuid()}";
        var creditMessage = new HttpRequestMessage(HttpMethod.Post, "/transactions/external-inbound")
        {
            Content = JsonContent.Create(new CreditWalletRequest(wallet!.AccountNumber, 5_000))
        };
        creditMessage.Headers.Add(CorrelationIdMiddleware.HeaderName, suppliedCorrelationId);

        var creditResponse = await client.SendAsync(creditMessage);
        Assert.Equal(HttpStatusCode.Created, creditResponse.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        var auditEntry = await db.AuditLog.SingleAsync(a => a.WalletId == wallet.WalletId);

        Assert.Equal(suppliedCorrelationId, auditEntry.CorrelationId);
    }

    [Fact]
    public async Task Transfer_WithClientSuppliedCorrelationId_WritesSameIdToBothAuditLogRows()
    {
        var fromClient = await CreateAuthenticatedClientAsync();
        var fromWalletResponse = await fromClient.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var fromWallet = await fromWalletResponse.Content.ReadFromJsonAsync<WalletResponse>();
        await fromClient.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(fromWallet!.AccountNumber, 10_000));

        var toClient = await CreateAuthenticatedClientAsync();
        var toWalletResponse = await toClient.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var toWallet = await toWalletResponse.Content.ReadFromJsonAsync<WalletResponse>();

        var suppliedCorrelationId = $"transfer-correlation-{Guid.NewGuid()}";
        var transferMessage = new HttpRequestMessage(HttpMethod.Post, "/transactions/internal-transfers")
        {
            Content = JsonContent.Create(new TransferRequest(fromWallet.WalletId, toWallet!.AccountNumber, 1_000))
        };
        transferMessage.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        transferMessage.Headers.Add(CorrelationIdMiddleware.HeaderName, suppliedCorrelationId);

        var transferResponse = await fromClient.SendAsync(transferMessage);
        Assert.Equal(HttpStatusCode.Created, transferResponse.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

        var debitAudit = await db.AuditLog.SingleAsync(a => a.WalletId == fromWallet.WalletId && a.Actor == "INTERNAL_TRANSFER");
        var creditAudit = await db.AuditLog.SingleAsync(a => a.WalletId == toWallet.WalletId && a.Actor == "INTERNAL_TRANSFER");

        Assert.Equal(suppliedCorrelationId, debitAudit.CorrelationId);
        Assert.Equal(suppliedCorrelationId, creditAudit.CorrelationId);
    }
}
