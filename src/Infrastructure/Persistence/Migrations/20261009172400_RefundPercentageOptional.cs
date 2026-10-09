using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// #202 (ADR-V026): a refund is stored as its amount; the percentage is only how a form computes it, so the app stops
    /// writing <c>Refunds.Percentage</c>. Expand-only: the column becomes nullable and every existing value stays as it
    /// was — nothing is recomputed. Dropping it is a later, owner-gated step. <c>Down</c> refills the rows the app has
    /// left empty from the refund's own amounts against its purchase (on the purchase's own currency side) before the
    /// not-null comes back, so a rollback never invents zeros.
    /// </summary>
    public partial class RefundPercentageOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "Percentage",
                table: "Refunds",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(5,2)",
                oldPrecision: 5,
                oldScale: 2);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The bypass is local to the migration's transaction: a non-superuser owner (Neon's) sees every household's
            // rows under the forced RLS policies only with it on (the INCOME-1 backfill does the same).
            migrationBuilder.Sql("""
                SELECT set_config('app.rls_bypass', 'on', true);
                UPDATE "Refunds" r
                SET "Percentage" = LEAST(100, GREATEST(0.01, ROUND(
                    CASE WHEN t."Currency" = 'USD' THEN r."AmountUsd" * 100 / NULLIF(t."AmountUsd", 0)
                         ELSE r."AmountCrc" * 100 / NULLIF(t."AmountCrc", 0) END, 2)))
                FROM "Transactions" t
                WHERE t."Id" = r."TransactionId" AND r."Percentage" IS NULL;
                """);

            migrationBuilder.AlterColumn<decimal>(
                name: "Percentage",
                table: "Refunds",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(5,2)",
                oldPrecision: 5,
                oldScale: 2,
                oldNullable: true);
        }
    }
}
