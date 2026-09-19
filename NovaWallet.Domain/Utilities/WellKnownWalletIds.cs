namespace NovaWallet.Domain;

public static class WellKnownWalletIds
{
    public const string SystemSettlementCustomerId = "SYSTEM_SETTLEMENT";

    // The double-entry leg for simulated external-inbound credits (WalletCreditService).
    // Kept at the original fixed id/account number used before the settlement account
    // was split, so existing references never move.
    public static readonly Guid NgnInboundSettlementAccount = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public const string NgnInboundSettlementAccountNumber = "0000000001";
    public const string InboundSettlementAccountType = "SETTLEMENT_INBOUND";

    // The double-entry leg for simulated external-outbound debits/reversals (OutboundService).
    // A separate wallet from the inbound leg so inbound and outbound exposure to the
    // (simulated) external network can be reasoned about and reconciled independently
    // instead of netting into one number.
    public static readonly Guid NgnOutboundSettlementAccount = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public const string NgnOutboundSettlementAccountNumber = "0000000002";
    public const string OutboundSettlementAccountType = "SETTLEMENT_OUTBOUND";
}
