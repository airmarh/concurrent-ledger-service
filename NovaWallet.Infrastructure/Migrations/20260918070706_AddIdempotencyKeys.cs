using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovaWallet.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE idempotency_keys (
                    customer_id text NOT NULL,
                    key text NOT NULL,
                    request_hash text NOT NULL,
                    status text NOT NULL,
                    status_code integer NULL,
                    result_json text NULL,
                    created_at timestamptz NOT NULL,
                    PRIMARY KEY (customer_id, key)
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS idempotency_keys;");
        }
    }
}
