using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovaWallet.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSettlementAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_negative_balance",
                table: "wallets",
                type: "boolean",
                nullable: false,
                defaultValue: false);
            
            migrationBuilder.Sql(
                """
                INSERT INTO wallets (wallet_id, customer_id, currency, account_type, balance_kobo, allow_negative_balance, created_at)
                VALUES ('00000000-0000-0000-0000-000000000001', 'SYSTEM_SETTLEMENT', 'NGN', 'SETTLEMENT', 0, true, now())
                ON CONFLICT (wallet_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM wallets WHERE wallet_id = '00000000-0000-0000-0000-000000000001';");

            migrationBuilder.DropColumn(
                name: "allow_negative_balance",
                table: "wallets");
        }
    }
}
