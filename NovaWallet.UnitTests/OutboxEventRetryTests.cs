using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class OutboxEventRetryTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RecordFailure_BelowMaxAttempts_SchedulesBackoffAndDoesNotFail()
    {
        var outboxEvent = OutboxEvent.Create(OutboxEventTypes.TransferCompleted, "{}", Now);

        outboxEvent.RecordFailure(Now, maxAttempts: 10, baseDelaySeconds: 5, maxDelaySeconds: 300);

        Assert.Equal(1, outboxEvent.AttemptCount);
        Assert.Null(outboxEvent.FailedAt);
        Assert.Equal(Now.AddSeconds(5), outboxEvent.NextAttemptAt);
    }

    [Fact]
    public void RecordFailure_BackoffGrowsExponentiallyPerAttempt()
    {
        var outboxEvent = OutboxEvent.Create(OutboxEventTypes.TransferCompleted, "{}", Now);

        outboxEvent.RecordFailure(Now, maxAttempts: 10, baseDelaySeconds: 5, maxDelaySeconds: 300); // attempt 1: 5s
        outboxEvent.RecordFailure(Now, maxAttempts: 10, baseDelaySeconds: 5, maxDelaySeconds: 300); // attempt 2: 10s
        outboxEvent.RecordFailure(Now, maxAttempts: 10, baseDelaySeconds: 5, maxDelaySeconds: 300); // attempt 3: 20s

        Assert.Equal(3, outboxEvent.AttemptCount);
        Assert.Equal(Now.AddSeconds(20), outboxEvent.NextAttemptAt);
    }

    [Fact]
    public void RecordFailure_BackoffIsCappedAtMaxDelay()
    {
        var outboxEvent = OutboxEvent.Create(OutboxEventTypes.TransferCompleted, "{}", Now);

        for (var i = 0; i < 6; i++)
            outboxEvent.RecordFailure(Now, maxAttempts: 10, baseDelaySeconds: 5, maxDelaySeconds: 60);

        // Uncapped this would be 5 * 2^5 = 160s; the cap must win.
        Assert.Equal(Now.AddSeconds(60), outboxEvent.NextAttemptAt);
    }

    [Fact]
    public void RecordFailure_ReachingMaxAttempts_SetsFailedAtAndStopsSchedulingRetries()
    {
        var outboxEvent = OutboxEvent.Create(OutboxEventTypes.TransferCompleted, "{}", Now);

        for (var i = 0; i < 2; i++)
            outboxEvent.RecordFailure(Now, maxAttempts: 3, baseDelaySeconds: 5, maxDelaySeconds: 300);
        Assert.Null(outboxEvent.FailedAt);

        outboxEvent.RecordFailure(Now, maxAttempts: 3, baseDelaySeconds: 5, maxDelaySeconds: 300);

        Assert.Equal(3, outboxEvent.AttemptCount);
        Assert.Equal(Now, outboxEvent.FailedAt);
    }
}
