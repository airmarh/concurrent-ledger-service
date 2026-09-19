using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Domain;

namespace NovaWallet.Api;

public static class InternalTransfersEndpoints
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapInternalTransfersEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/transactions/internal-transfers", async (
                [FromBody] TransferRequest request,
                ClaimsPrincipal user,
                HttpContext httpContext,
                IWalletRepository walletRepository,
                ITransferService transferService,
                IIdempotencyStore idempotencyStore,
                CancellationToken ct) =>
            {
                if (!httpContext.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var keyHeader)
                    || string.IsNullOrWhiteSpace(keyHeader))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        [IdempotencyKeyHeader] = ["The Idempotency-Key header is required on transfer requests."]
                    });
                }

                var idempotencyKey = keyHeader.ToString();
                var customerId = user.FindFirstValue("customer_id")!;
                var requestHash = IdempotencyHasher.ComputeHash(JsonSerializer.Serialize(request));

                var claim = await idempotencyStore.ClaimAsync(customerId, idempotencyKey, requestHash, ct);
                switch (claim.Outcome)
                {
                    case IdempotencyOutcome.ReplayCompleted:
                        return Results.Content(claim.ResultJson!, "application/json", statusCode: claim.StatusCode);
                    case IdempotencyOutcome.StillProcessing:
                        throw new IdempotencyKeyProcessingException(idempotencyKey);
                }

                try
                {
                    var fromWallet = await walletRepository.GetByIdAsync(request.FromWalletId, ct)
                                      ?? throw new WalletNotFoundException(request.FromWalletId);

                    if (fromWallet.CustomerId != customerId)
                        throw new ForbiddenWalletAccessException(request.FromWalletId);

                    
                    var toWallet = await walletRepository.GetByAccountNumberAsync(request.ToAccountNumber, ct)
                                    ?? throw new WalletNotFoundException(request.ToAccountNumber);

                    var correlationId = httpContext.GetCorrelationId();

                    var result = await transferService.TransferAsync(
                        request.FromWalletId, toWallet.WalletId, request.AmountKobo, correlationId, ct);

                    var response = new TransferResponse(
                        result.DebitTransactionId,
                        result.CreditTransactionId,
                        result.FromWalletId,
                        result.ToWalletId,
                        result.AmountKobo,
                        result.FromBalanceKobo,
                        result.ToBalanceKobo);

                    await idempotencyStore.CompleteAsync(
                        customerId, idempotencyKey, StatusCodes.Status201Created,
                        JsonSerializer.Serialize(response, ResponseJsonOptions), ct);

                    return Results.Created($"/wallets/{result.FromWalletId}", response);
                }
                catch
                {
                    await idempotencyStore.ReleaseAsync(customerId, idempotencyKey, ct);
                    throw;
                }
            })
            .RequireAuthorization()
            .RequireRateLimiting("transfers")
            .WithName("CreateInternalTransfer")
            .WithTags("Transactions")
            .WithOpenApi();
    }
}

public record TransferRequest(Guid FromWalletId, string ToAccountNumber, long AmountKobo);

public record TransferResponse(
    Guid DebitTransactionId,
    Guid CreditTransactionId,
    Guid FromWalletId,
    Guid ToWalletId,
    long AmountKobo,
    long FromBalanceKobo,
    long ToBalanceKobo);
