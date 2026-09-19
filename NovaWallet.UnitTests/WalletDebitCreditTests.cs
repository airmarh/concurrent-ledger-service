using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class WalletDebitCreditTests
{
    [Fact]
    public void Debit_WithSufficientBalance_ReducesBalance()
    {
        var wallet = Wallet.Create("customer-1");
        wallet.Credit(10_000);

        wallet.Debit(4_000);

        Assert.Equal(6_000, wallet.BalanceKobo);
    }

    [Fact]
    public void Debit_WithInsufficientBalance_ThrowsAndLeavesBalanceUnchanged()
    {
        var wallet = Wallet.Create("customer-1");
        wallet.Credit(1_000);

        Assert.Throws<InsufficientFundsException>(() => wallet.Debit(1_001));
        Assert.Equal(1_000, wallet.BalanceKobo);
    }

    [Fact]
    public void Debit_ExactBalance_DrainsToZero()
    {
        var wallet = Wallet.Create("customer-1");
        wallet.Credit(5_000);

        wallet.Debit(5_000);

        Assert.Equal(0, wallet.BalanceKobo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Debit_WithNonPositiveAmount_ThrowsInvalidAmountException(long amountKobo)
    {
        var wallet = Wallet.Create("customer-1");
        wallet.Credit(10_000);

        Assert.Throws<InvalidAmountException>(() => wallet.Debit(amountKobo));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Credit_WithNonPositiveAmount_ThrowsInvalidAmountException(long amountKobo)
    {
        var wallet = Wallet.Create("customer-1");

        Assert.Throws<InvalidAmountException>(() => wallet.Credit(amountKobo));
    }

    [Fact]
    public void Debit_OnSettlementAccount_AllowsNegativeBalance()
    {
        var settlementAccount = Wallet.CreateSettlementAccount(
            Guid.NewGuid(), WellKnownWalletIds.NgnInboundSettlementAccountNumber, WellKnownWalletIds.InboundSettlementAccountType, "NGN");

        settlementAccount.Debit(5_000);

        Assert.Equal(-5_000, settlementAccount.BalanceKobo);
    }

    [Fact]
    public void Debit_OnOrdinaryWallet_StillRejectsNegativeBalance()
    {
        var wallet = Wallet.Create("customer-1");

        Assert.Throws<InsufficientFundsException>(() => wallet.Debit(1));
        Assert.Equal(0, wallet.BalanceKobo);
    }
}
