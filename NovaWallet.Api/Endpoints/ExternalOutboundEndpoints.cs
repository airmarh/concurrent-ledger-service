using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Domain;

namespace NovaWallet.Api;

public static class ExternalOutboundEndpoints
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapExternalOutboundEndpoints(this IEndpointRouteBuilder app)
    {
        // Customer-initiated like internal-transfers, so it gets the same ownership
        // check, Idempotency-Key, and rate limiting — only the counterparty differs
        // (the settlement account, standing in for the NIP network).
        app.MapPost("/transactions/external-outbound", async (
                [FromBody] ExternalOutboundDebitRequest request,
                ClaimsPrincipal user,
                HttpContext httpContext,
                IWalletRepository walletRepository,
                IOutboundService outboundService,
                IIdempotencyStore idempotencyStore,
                CancellationToken ct) =>
            {
                if (!httpContext.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var keyHeader)
                    || string.IsNullOrWhiteSpace(keyHeader))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        [IdempotencyKeyHeader] = ["The Idempotency-Key header is required on external-outbound requests."]
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
                    var wallet = await walletRepository.GetByIdAsync(request.WalletId, ct)
                                 ?? throw new WalletNotFoundException(request.WalletId);

                    if (wallet.CustomerId != customerId)
                        throw new ForbiddenWalletAccessException(request.WalletId);

                    var correlationId = httpContext.GetCorrelationId();

                    var result = await outboundService.DebitAsync(
                        request.WalletId,
                        request.AmountKobo,
                        actor: "EXTERNAL_OUTBOUND",
                        correlationId: correlationId,
                        ct: ct);

                    var response = new ExternalOutboundDebitResponse(result.TransactionId, result.WalletId, result.BalanceKobo);

                    await idempotencyStore.CompleteAsync(
                        customerId, idempotencyKey, StatusCodes.Status201Created,
                        JsonSerializer.Serialize(response, ResponseJsonOptions), ct);

                    return Results.Created($"/wallets/{result.WalletId}", response);
                }
                catch
                {
                    await idempotencyStore.ReleaseAsync(customerId, idempotencyKey, ct);
                    throw;
                }
            })
            .RequireAuthorization()
            .RequireRateLimiting("transfers")
            .WithName("CreateExternalOutboundDebit")
            .WithSummary("Debits the caller's wallet to simulate an outbound third-party (interbank/NIP) transfer.")
            .WithTags("Transactions")
            .WithOpenApi();

        // Simulates the NIP switch reporting a failed outbound transfer after the fact —
        // not customer-initiated, so no ownership check, Idempotency-Key, or rate
        // limiting: transactionId plus the transaction_reversals primary key already
        // guarantee a transaction can only be reversed once.
        app.MapPost("/transactions/external-outbound/{transactionId:guid}/reversal", async (
                Guid transactionId,
                [FromBody] ReverseExternalOutboundDebitRequest request,
                HttpContext httpContext,
                IOutboundService outboundService,
                CancellationToken ct) =>
            {
                var correlationId = httpContext.GetCorrelationId();

                var result = await outboundService.ReverseAsync(
                    transactionId,
                    request.Reason,
                    actor: "EXTERNAL_OUTBOUND_REVERSAL",
                    correlationId: correlationId,
                    ct: ct);

                return Results.Created(
                    $"/wallets/{result.WalletId}",
                    new ExternalOutboundReversalResponse(
                        result.OriginalTransactionId, result.ReversalTransactionId, result.WalletId, result.BalanceKobo));
            })
            .RequireAuthorization()
            .WithName("ReverseExternalOutboundDebit")
            .WithSummary("Simulates the NIP switch reporting that an outbound transfer failed, reversing its debit.")
            .WithTags("Transactions")
            .WithOpenApi();
    }
}

public record ExternalOutboundDebitRequest(Guid WalletId, long AmountKobo);

public record ExternalOutboundDebitResponse(Guid TransactionId, Guid WalletId, long BalanceKobo);

public record ReverseExternalOutboundDebitRequest(string Reason);

public record ExternalOutboundReversalResponse(
    Guid OriginalTransactionId, Guid ReversalTransactionId, Guid WalletId, long BalanceKobo);
