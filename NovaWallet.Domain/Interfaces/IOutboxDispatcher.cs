namespace NovaWallet.Domain;

public interface IOutboxDispatcher
{
    // Returns the number of events dispatched by this call.
    // A return of 0 is a normal, expected outcome, not an error.
    Task<int> DispatchPendingAsync(CancellationToken ct = default);
}
