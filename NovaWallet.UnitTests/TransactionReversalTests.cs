using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class TransactionReversalTests
{
    [Fact]
    public void Create_WithReason_Succeeds()
    {
        var originalTransactionId = Guid.NewGuid();
        var reversalTransactionId = Guid.NewGuid();

        var reversal = TransactionReversal.Create(originalTransactionId, reversalTransactionId, "ACCOUNT_NOT_FOUND");

        Assert.Equal(originalTransactionId, reversal.OriginalTransactionId);
        Assert.Equal(reversalTransactionId, reversal.ReversalTransactionId);
        Assert.Equal("ACCOUNT_NOT_FOUND", reversal.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutReason_ThrowsArgumentException(string? reason)
    {
        Assert.Throws<ArgumentException>(() =>
            TransactionReversal.Create(Guid.NewGuid(), Guid.NewGuid(), reason!));
    }
}
