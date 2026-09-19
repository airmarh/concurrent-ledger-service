using Microsoft.AspNetCore.Diagnostics;
using NovaWallet.Domain;

namespace NovaWallet.Api;

public class ProblemDetailsExceptionHandler(ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, errorCode, title) = exception switch
        {
            WalletNotFoundException ex => (StatusCodes.Status404NotFound, ex.ErrorCode, ex.Message),
            TransactionNotFoundException ex => (StatusCodes.Status404NotFound, ex.ErrorCode, ex.Message),
            DuplicateWalletException ex => (StatusCodes.Status409Conflict, ex.ErrorCode, ex.Message),
            ForbiddenWalletAccessException ex => (StatusCodes.Status403Forbidden, ex.ErrorCode, ex.Message),
            IdempotencyKeyConflictException ex => (StatusCodes.Status409Conflict, ex.ErrorCode, ex.Message),
            IdempotencyKeyProcessingException ex => (StatusCodes.Status409Conflict, ex.ErrorCode, ex.Message),
            TransactionAlreadyReversedException ex => (StatusCodes.Status409Conflict, ex.ErrorCode, ex.Message),
            DailyLimitExceededException ex => (StatusCodes.Status429TooManyRequests, ex.ErrorCode, ex.Message),
            DomainException ex => (StatusCodes.Status422UnprocessableEntity, ex.ErrorCode, ex.Message),
            ArgumentException ex => (StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", ex.Message),
            _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred.")
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception");
        }

        await ProblemDetailsWriter.WriteAsync(httpContext, status, errorCode, title, cancellationToken);

        return true;
    }
}
