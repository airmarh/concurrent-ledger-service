using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovaWallet.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletAccountNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "account_number",
                table: "wallets",
                type: "character(10)",
                fixedLength: true,
                maxLength: 10,
                nullable: true);
            
            migrationBuilder.Sql(
                """
                UPDATE wallets
                SET account_number = lpad((floor(random() * 9000000000) + 1000000000)::bigint::text, 10, '0')
                WHERE account_number IS NULL;
                """);

            migrationBuilder.Sql(
                "UPDATE wallets SET account_number = '0000000001' WHERE wallet_id = '00000000-0000-0000-0000-000000000001';");

            migrationBuilder.AlterColumn<string>(
                name: "account_number",
                table: "wallets",
                type: "character(10)",
                fixedLength: true,
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character(10)",
                oldFixedLength: true,
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_wallets_account_number",
                table: "wallets",
                column: "account_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_wallets_account_number",
                table: "wallets");

            migrationBuilder.DropColumn(
                name: "account_number",
                table: "wallets");
        }
    }
}
