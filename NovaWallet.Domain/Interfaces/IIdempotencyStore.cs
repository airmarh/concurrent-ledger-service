namespace NovaWallet.Domain;

public interface IIdempotencyStore
{
    // Throws IdempotencyKeyConflictException on a payload mismatch; otherwise returns
    // Claimed (this caller processes it) or a cached replay result.
    Task<IdempotencyClaim> ClaimAsync(string customerId, string key, string requestHash, CancellationToken ct = default);

    Task CompleteAsync(string customerId, string key, int statusCode, string resultJson, CancellationToken ct = default);

    // Releases a claim that failed to complete, freeing the key for a legitimate retry.
    Task ReleaseAsync(string customerId, string key, CancellationToken ct = default);
}

public enum IdempotencyOutcome
{
    Claimed,
    ReplayCompleted,
    StillProcessing
}

public record IdempotencyClaim(IdempotencyOutcome Outcome, int? StatusCode = null, string? ResultJson = null);
