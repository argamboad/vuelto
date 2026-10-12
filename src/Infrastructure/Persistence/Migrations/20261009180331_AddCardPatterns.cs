using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// #210 (ADR-V027): a masked number whose digits are not the last four (BN pagos print <c>XXXXXXXXXXX8755X</c> for a
    /// card ending 7558) is mapped to its card once, in the review queue, and remembered here. A new, empty,
    /// <c>ITenantScoped</c> table — its RLS policy ships in this migration (ADR-020) or <c>RlsMigrationGateTests</c>
    /// fails. Nothing existing is touched.
    /// </summary>
    public partial class AddCardPatterns : Migration
    {
        private const string Table = "CardPatterns";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CardPatterns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Pattern = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardPatterns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardPatterns_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardPatterns_CardId",
                table: "CardPatterns",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_CardPatterns_TenantId_Pattern",
                table: "CardPatterns",
                columns: new[] { "TenantId", "Pattern" },
                unique: true);

            // Tenancy backstop (ADR-020): enable + force RLS and create the fail-closed policy.
            foreach (var statement in RlsDdl.StatementsFor(Table, "TenantId"))
                migrationBuilder.Sql(statement);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data loss on Down: the remembered pattern → card answers go with the table. Acceptable — they are
            // re-asked in the review queue the next time such a voucher arrives; no transaction loses its card.
            migrationBuilder.Sql($"""DROP POLICY IF EXISTS {RlsDdl.PolicyName} ON "{Table}";""");
            migrationBuilder.DropTable(
                name: "CardPatterns");
        }
    }
}
