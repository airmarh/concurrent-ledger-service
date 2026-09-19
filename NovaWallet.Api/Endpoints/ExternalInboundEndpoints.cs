using Microsoft.AspNetCore.Mvc;
using NovaWallet.Domain;

namespace NovaWallet.Api;

public static class ExternalInboundEndpoints
{
    public static void MapExternalInboundEndpoints(this IEndpointRouteBuilder app)
    {
        // Addressed by account number, not wallet_id — the identifier a real sending
        // bank would actually have, not an internal ledger ID.
        app.MapPost("/transactions/external-inbound", async (
                [FromBody] CreditWalletRequest request,
                HttpContext httpContext,
                IWalletRepository walletRepository,
                IWalletCreditService creditService,
                CancellationToken ct) =>
            {
                var wallet = await walletRepository.GetByAccountNumberAsync(request.AccountNumber, ct)
                             ?? throw new WalletNotFoundException(request.AccountNumber);

                var correlationId = httpContext.GetCorrelationId();

                var result = await creditService.CreditAsync(
                    wallet.WalletId,
                    request.AmountKobo,
                    actor: "EXTERNAL_INBOUND",
                    correlationId: correlationId,
                    ct: ct);

                return Results.Created(
                    $"/wallets/{wallet.WalletId}",
                    new CreditResponse(result.TransactionId, result.WalletId, result.BalanceKobo));
            })
            .RequireAuthorization()
            .WithName("CreateExternalInboundCredit")
            .WithSummary("Simulates a third-party (interbank/NIP) credit landing in the wallet, addressed by account number.")
            .WithTags("Transactions")
            .WithOpenApi();
    }
}

public record CreditWalletRequest(string AccountNumber, long AmountKobo);

public record CreditResponse(Guid TransactionId, Guid WalletId, long BalanceKobo);
