using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class TransactionTests
{
    [Fact]
    public void Create_WithPositiveAmount_Succeeds()
    {
        var walletId = Guid.NewGuid();

        var transaction = Transaction.Create(walletId, 5_000, TransactionDirection.Credit);

        Assert.Equal(walletId, transaction.WalletId);
        Assert.Equal(5_000, transaction.AmountKobo);
        Assert.Equal(TransactionDirection.Credit, transaction.Direction);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100_000)]
    public void Create_WithNonPositiveAmount_ThrowsInvalidAmountException(long amountKobo)
    {
        Assert.Throws<InvalidAmountException>(() =>
            Transaction.Create(Guid.NewGuid(), amountKobo, TransactionDirection.Credit));
    }
}
