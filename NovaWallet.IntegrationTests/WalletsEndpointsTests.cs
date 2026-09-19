using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NovaWallet.Api;
using NovaWallet.Domain;

namespace NovaWallet.IntegrationTests;

public class WalletsEndpointsTests(NovaWalletApiFactory factory) : IClassFixture<NovaWalletApiFactory>
{
    private async Task<HttpClient> CreateAuthenticatedClientAsync(string customerId)
    {
        var client = factory.CreateClient();
        var tokenResponse = await client.PostAsJsonAsync("/auth/token", new TokenRequest(customerId));
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private async Task<(HttpClient Client, Guid WalletId, string AccountNumber)> CreateAuthenticatedClientWithWalletAsync()
    {
        var client = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");

        var createResponse = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var wallet = await createResponse.Content.ReadFromJsonAsync<WalletResponse>();

        return (client, wallet!.WalletId, wallet.AccountNumber);
    }

    [Theory]
    [InlineData(WellKnownWalletIds.InboundSettlementAccountType)]
    [InlineData(WellKnownWalletIds.OutboundSettlementAccountType)]
    public async Task CreateWallet_WithReservedSettlementAccountType_ReturnsUnprocessableEntity(string accountType)
    {
        var client = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");

        var response = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(Currency: null, AccountType: accountType));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateWallet_ThenGetBalance_ReturnsZeroBalanceNgn()
    {
        var client = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");

        var createResponse = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(Currency: null, AccountType: null));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<WalletResponse>();

        var getResponse = await client.GetAsync($"/wallets/{created!.WalletId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var wallet = await getResponse.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(0, wallet!.BalanceKobo);
        Assert.Equal("NGN", wallet.Currency);
        Assert.Equal("SAVINGS", wallet.AccountType);
    }

    [Fact]
    public async Task ListMyWallets_ReturnsOnlyTheCallersOwnWallets()
    {
        var client = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");
        var savings = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var savingsWallet = await savings.Content.ReadFromJsonAsync<WalletResponse>();
        var current = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, "CURRENT"));
        var currentWallet = await current.Content.ReadFromJsonAsync<WalletResponse>();

        var otherClient = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");
        await otherClient.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));

        var response = await client.GetAsync("/wallets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var wallets = await response.Content.ReadFromJsonAsync<List<WalletResponse>>();
        Assert.Equal(2, wallets!.Count);
        Assert.Contains(wallets, w => w.WalletId == savingsWallet!.WalletId);
        Assert.Contains(wallets, w => w.WalletId == currentWallet!.WalletId);
    }

    [Fact]
    public async Task ListMyWallets_NoWalletsYet_ReturnsEmptyArray()
    {
        var client = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");

        var response = await client.GetAsync("/wallets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var wallets = await response.Content.ReadFromJsonAsync<List<WalletResponse>>();
        Assert.Empty(wallets!);
    }

    [Fact]
    public async Task CreateWallet_AssignsA10DigitAccountNumber_DistinctAcrossWallets()
    {
        var clientA = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");
        var clientB = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");

        var responseA = await clientA.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var walletA = await responseA.Content.ReadFromJsonAsync<WalletResponse>();

        var responseB = await clientB.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var walletB = await responseB.Content.ReadFromJsonAsync<WalletResponse>();

        Assert.Equal(10, walletA!.AccountNumber.Length);
        Assert.True(walletA.AccountNumber.All(char.IsDigit));
        Assert.NotEqual(walletA.AccountNumber, walletB!.AccountNumber);
    }

    [Fact]
    public async Task GetBalanceByAccountNumber_OwnedWallet_ReturnsTheSameWalletAsGetById()
    {
        var client = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");
        var createResponse = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var created = await createResponse.Content.ReadFromJsonAsync<WalletResponse>();

        var response = await client.GetAsync($"/wallets/by-account-number/{created!.AccountNumber}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var wallet = await response.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.Equal(created.WalletId, wallet!.WalletId);
    }

    [Fact]
    public async Task GetBalanceByAccountNumber_UnknownAccountNumber_ReturnsNotFound()
    {
        var client = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");

        var response = await client.GetAsync("/wallets/by-account-number/9999999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetBalanceByAccountNumber_WalletNotOwnedByCaller_ReturnsForbidden()
    {
        var ownerClient = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");
        var createResponse = await ownerClient.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var wallet = await createResponse.Content.ReadFromJsonAsync<WalletResponse>();

        var otherClient = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");
        var response = await otherClient.GetAsync($"/wallets/by-account-number/{wallet!.AccountNumber}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateWallet_DuplicateCustomerCurrencyAccountType_ReturnsConflict()
    {
        var client = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");

        var first = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetBalance_UnknownWallet_ReturnsNotFoundProblemDetails()
    {
        var client = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");

        var response = await client.GetAsync($"/wallets/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetBalance_WalletNotOwnedByCaller_ReturnsForbidden()
    {
        var ownerClient = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");
        var createResponse = await ownerClient.PostAsJsonAsync("/wallets", new CreateWalletRequest(null, null));
        var wallet = await createResponse.Content.ReadFromJsonAsync<WalletResponse>();

        var otherClient = await CreateAuthenticatedClientAsync($"customer-{Guid.NewGuid()}");
        var response = await otherClient.GetAsync($"/wallets/{wallet!.WalletId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/wallets/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TransactionHistory_EmptyWallet_ReturnsEmptyPageNotError()
    {
        var (client, walletId, _) = await CreateAuthenticatedClientWithWalletAsync();

        var response = await client.GetAsync($"/wallets/{walletId}/transactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = await response.Content.ReadFromJsonAsync<TransactionHistoryResponse>();
        Assert.Empty(history!.Items);
        Assert.Equal(0, history.TotalCount);
    }

    [Fact]
    public async Task TransactionHistory_ReturnsTransactionsNewestFirst()
    {
        var (client, walletId, accountNumber) = await CreateAuthenticatedClientWithWalletAsync();

        for (var i = 1; i <= 5; i++)
        {
            var creditResponse = await client.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(accountNumber, 1_000 * i));
            Assert.Equal(HttpStatusCode.Created, creditResponse.StatusCode);
        }

        var historyResponse = await client.GetAsync($"/wallets/{walletId}/transactions?page=1&pageSize=20");
        var history = await historyResponse.Content.ReadFromJsonAsync<TransactionHistoryResponse>();

        Assert.Equal(5, history!.TotalCount);
        Assert.Equal(5, history.Items.Count);
        Assert.Equal(5_000, history.Items[0].AmountKobo);
        Assert.Equal(1_000, history.Items[^1].AmountKobo);
    }

    [Fact]
    public async Task TransactionHistory_Pagination_FirstAndLastPageBoundaries()
    {
        var (client, walletId, accountNumber) = await CreateAuthenticatedClientWithWalletAsync();

        for (var i = 1; i <= 5; i++)
        {
            await client.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(accountNumber, 1_000 * i));
        }

        var firstPageResponse = await client.GetAsync($"/wallets/{walletId}/transactions?page=1&pageSize=2");
        var firstPage = await firstPageResponse.Content.ReadFromJsonAsync<TransactionHistoryResponse>();
        Assert.Equal(2, firstPage!.Items.Count);
        Assert.Equal(5_000, firstPage.Items[0].AmountKobo);
        Assert.Equal(4_000, firstPage.Items[1].AmountKobo);
        Assert.Equal(3, firstPage.TotalPages);

        var lastPageResponse = await client.GetAsync($"/wallets/{walletId}/transactions?page=3&pageSize=2");
        var lastPage = await lastPageResponse.Content.ReadFromJsonAsync<TransactionHistoryResponse>();
        Assert.Single(lastPage!.Items);
        Assert.Equal(1_000, lastPage.Items[0].AmountKobo);
    }

    [Fact]
    public async Task TransactionHistory_OnlyReturnsTransactionsForRequestedWallet()
    {
        var (clientA, walletA, accountNumberA) = await CreateAuthenticatedClientWithWalletAsync();
        await clientA.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(accountNumberA, 1_000));

        var (clientB, walletB, accountNumberB) = await CreateAuthenticatedClientWithWalletAsync();
        await clientB.PostAsJsonAsync("/transactions/external-inbound", new CreditWalletRequest(accountNumberB, 2_000));

        var response = await clientA.GetAsync($"/wallets/{walletA}/transactions");
        var history = await response.Content.ReadFromJsonAsync<TransactionHistoryResponse>();

        var item = Assert.Single(history!.Items);
        Assert.Equal(walletA, item.WalletId);
    }

    [Fact]
    public async Task TransactionHistory_UnknownWallet_ReturnsNotFound()
    {
        var (client, _, _) = await CreateAuthenticatedClientWithWalletAsync();

        var response = await client.GetAsync($"/wallets/{Guid.NewGuid()}/transactions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TransactionHistory_WalletNotOwnedByCaller_ReturnsForbidden()
    {
        var (_, walletId, _) = await CreateAuthenticatedClientWithWalletAsync();

        var (otherClient, _, _) = await CreateAuthenticatedClientWithWalletAsync();
        var response = await otherClient.GetAsync($"/wallets/{walletId}/transactions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
