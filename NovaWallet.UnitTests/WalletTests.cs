using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class WalletTests
{
    [Fact]
    public void Create_StartsAtZeroBalance()
    {
        var wallet = Wallet.Create("customer-1");

        Assert.Equal(0, wallet.BalanceKobo);
    }

    [Fact]
    public void Create_DefaultsToNgnSavings()
    {
        var wallet = Wallet.Create("customer-1");

        Assert.Equal("NGN", wallet.Currency);
        Assert.Equal("SAVINGS", wallet.AccountType);
    }

    [Fact]
    public void Create_AssignsUniqueWalletId()
    {
        var walletA = Wallet.Create("customer-1");
        var walletB = Wallet.Create("customer-1");

        Assert.NotEqual(walletA.WalletId, walletB.WalletId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsMissingCustomerId(string? customerId)
    {
        Assert.Throws<ArgumentException>(() => Wallet.Create(customerId!));
    }

    [Fact]
    public void Create_AssignsA10DigitAccountNumber()
    {
        var wallet = Wallet.Create("customer-1");

        Assert.Equal(10, wallet.AccountNumber.Length);
        Assert.True(wallet.AccountNumber.All(char.IsDigit));
    }

    [Fact]
    public void Create_AssignsUniqueAccountNumbers()
    {
        var walletA = Wallet.Create("customer-1");
        var walletB = Wallet.Create("customer-1");

        Assert.NotEqual(walletA.AccountNumber, walletB.AccountNumber);
    }

    [Fact]
    public void RegenerateAccountNumber_ReplacesItWithAnotherValidValue()
    {
        var wallet = Wallet.Create("customer-1");
        var original = wallet.AccountNumber;

        wallet.RegenerateAccountNumber();

        Assert.NotEqual(original, wallet.AccountNumber);
        Assert.Equal(10, wallet.AccountNumber.Length);
        Assert.True(wallet.AccountNumber.All(char.IsDigit));
    }

    [Fact]
    public void CreateSettlementAccount_InboundUsesTheWellKnownAccountNumber()
    {
        var settlementAccount = Wallet.CreateSettlementAccount(
            WellKnownWalletIds.NgnInboundSettlementAccount,
            WellKnownWalletIds.NgnInboundSettlementAccountNumber,
            WellKnownWalletIds.InboundSettlementAccountType,
            "NGN");

        Assert.Equal(WellKnownWalletIds.NgnInboundSettlementAccountNumber, settlementAccount.AccountNumber);
        Assert.Equal(WellKnownWalletIds.InboundSettlementAccountType, settlementAccount.AccountType);
    }

    [Fact]
    public void CreateSettlementAccount_OutboundUsesTheWellKnownAccountNumber()
    {
        var settlementAccount = Wallet.CreateSettlementAccount(
            WellKnownWalletIds.NgnOutboundSettlementAccount,
            WellKnownWalletIds.NgnOutboundSettlementAccountNumber,
            WellKnownWalletIds.OutboundSettlementAccountType,
            "NGN");

        Assert.Equal(WellKnownWalletIds.NgnOutboundSettlementAccountNumber, settlementAccount.AccountNumber);
        Assert.Equal(WellKnownWalletIds.OutboundSettlementAccountType, settlementAccount.AccountType);
    }
}
