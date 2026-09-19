using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovaWallet.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SplitSettlementAccountIntoInboundAndOutbound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE wallets SET account_type = 'SETTLEMENT_INBOUND' WHERE wallet_id = '00000000-0000-0000-0000-000000000001';");

            migrationBuilder.Sql(
                """
                INSERT INTO wallets (wallet_id, account_number, customer_id, currency, account_type, balance_kobo, allow_negative_balance, created_at)
                VALUES ('00000000-0000-0000-0000-000000000002', '0000000002', 'SYSTEM_SETTLEMENT', 'NGN', 'SETTLEMENT_OUTBOUND', 0, true, now())
                ON CONFLICT (wallet_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM wallets WHERE wallet_id = '00000000-0000-0000-0000-000000000002';");

            migrationBuilder.Sql(
                "UPDATE wallets SET account_type = 'SETTLEMENT' WHERE wallet_id = '00000000-0000-0000-0000-000000000001';");
        }
    }
}
