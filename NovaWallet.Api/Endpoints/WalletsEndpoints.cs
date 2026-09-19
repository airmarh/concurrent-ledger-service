using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Domain;

namespace NovaWallet.Api;

public static class WalletsEndpoints
{
    public static void MapWalletsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/wallets").RequireAuthorization().WithTags("Wallets");

        group.MapGet("/", async (
                ClaimsPrincipal user,
                IWalletRepository repository,
                CancellationToken ct) =>
            {
                var customerId = user.FindFirstValue("customer_id")!;

                var wallets = await repository.GetByCustomerIdAsync(customerId, ct);

                return Results.Ok(wallets.Select(ToResponse).ToList());
            })
            .WithName("ListMyWallets")
            .WithSummary("Lists every wallet owned by the caller — the only way to recover a wallet_id after creation if the client didn't keep it.")
            .WithOpenApi();

        group.MapPost("/", async (
                [FromBody] CreateWalletRequest request,
                ClaimsPrincipal user,
                IWalletRepository repository,
                CancellationToken ct) =>
            {
                var customerId = user.FindFirstValue("customer_id")!;
                var currency = string.IsNullOrWhiteSpace(request.Currency) ? "NGN" : request.Currency;
                var accountType = string.IsNullOrWhiteSpace(request.AccountType) ? "SAVINGS" : request.AccountType;

                if (string.Equals(accountType, WellKnownWalletIds.InboundSettlementAccountType, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(accountType, WellKnownWalletIds.OutboundSettlementAccountType, StringComparison.OrdinalIgnoreCase))
                    throw new ReservedAccountTypeException(accountType);

                if (await repository.ExistsAsync(customerId, currency, accountType, ct))
                {
                    throw new DuplicateWalletException(customerId, currency, accountType);
                }

                var wallet = Wallet.Create(customerId, currency, accountType);
                await repository.AddAsync(wallet, ct);

                var response = ToResponse(wallet);
                return Results.Created($"/wallets/{wallet.WalletId}", response);
            })
            .WithName("CreateWallet")
            .WithOpenApi();

        group.MapGet("/{walletId:guid}", async (
                Guid walletId,
                ClaimsPrincipal user,
                IWalletRepository repository,
                CancellationToken ct) =>
            {
                var customerId = user.FindFirstValue("customer_id")!;

                var wallet = await repository.GetByIdAsync(walletId, ct)
                             ?? throw new WalletNotFoundException(walletId);

                if (wallet.CustomerId != customerId)
                    throw new ForbiddenWalletAccessException(walletId);

                return Results.Ok(ToResponse(wallet));
            })
            .WithName("GetWalletById")
            .WithOpenApi();

        group.MapGet("/by-account-number/{accountNumber}", async (
                string accountNumber,
                ClaimsPrincipal user,
                IWalletRepository repository,
                CancellationToken ct) =>
            {
                var customerId = user.FindFirstValue("customer_id")!;

                var wallet = await repository.GetByAccountNumberAsync(accountNumber, ct)
                             ?? throw new WalletNotFoundException(accountNumber);

                if (wallet.CustomerId != customerId)
                    throw new ForbiddenWalletAccessException(wallet.WalletId);

                return Results.Ok(ToResponse(wallet));
            })
            .WithName("GetWalletByAccountNumber")
            .WithSummary("Resolves a customer-facing account number to the wallet it identifies.")
            .WithOpenApi();

        group.MapGet("/{walletId:guid}/transactions", async (
                Guid walletId,
                int? page,
                int? pageSize,
                ClaimsPrincipal user,
                IWalletRepository walletRepository,
                ITransactionHistoryRepository transactionHistoryRepository,
                CancellationToken ct) =>
            {
                var customerId = user.FindFirstValue("customer_id")!;

                var wallet = await walletRepository.GetByIdAsync(walletId, ct)
                             ?? throw new WalletNotFoundException(walletId);

                if (wallet.CustomerId != customerId)
                    throw new ForbiddenWalletAccessException(walletId);

                var query = TransactionHistoryQuery.Create(page, pageSize);
                var (items, totalCount) = await transactionHistoryRepository.GetPageAsync(walletId, query, ct);

                var response = new TransactionHistoryResponse(
                    query.Page,
                    query.PageSize,
                    totalCount,
                    (int)Math.Ceiling(totalCount / (double)query.PageSize),
                    items.Select(ToTransactionResponse).ToList());

                return Results.Ok(response);
            })
            .WithName("GetWalletTransactionHistory")
            .WithSummary("Returns a page/pageSize-paginated transaction history for the wallet, newest first.")
            .WithOpenApi();
    }

    private static WalletResponse ToResponse(Wallet wallet) => new(
        wallet.WalletId,
        wallet.AccountNumber,
        wallet.CustomerId,
        wallet.Currency,
        wallet.AccountType,
        wallet.BalanceKobo,
        wallet.CreatedAt);

    private static TransactionResponse ToTransactionResponse(Transaction transaction) => new(
        transaction.Id,
        transaction.WalletId,
        transaction.CounterpartyWalletId,
        transaction.AmountKobo,
        transaction.Direction.ToString(),
        transaction.CreatedAt);
}

public record CreateWalletRequest(string? Currency, string? AccountType);

public record WalletResponse(
    Guid WalletId,
    string AccountNumber,
    string CustomerId,
    string Currency,
    string AccountType,
    long BalanceKobo,
    DateTime CreatedAt);

public record TransactionResponse(
    Guid Id,
    Guid WalletId,
    Guid? CounterpartyWalletId,
    long AmountKobo,
    string Direction,
    DateTime CreatedAt);

public record TransactionHistoryResponse(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<TransactionResponse> Items);
