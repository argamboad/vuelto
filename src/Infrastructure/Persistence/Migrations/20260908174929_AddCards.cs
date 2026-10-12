using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// CARDS-1 (ADR-V021): the household's payment cards and every (brand, last four) each is known by, plus the
    /// optional card on a transaction and the brand a voucher printed. Both new tables are <c>ITenantScoped</c>, so
    /// their RLS policies ship here (ADR-020; add-a-slice step 4), generated from <see cref="RlsDdl"/> — the
    /// <c>RlsMigrationGateTests</c> parity gate fails CI otherwise.
    /// </summary>
    public partial class AddCards : Migration
    {
        private static readonly string[] Tables = ["Cards", "CardIdentities"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CardId",
                table: "Transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CardBrand",
                table: "PendingVouchers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Cards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Brand = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    BankId = table.Column<Guid>(type: "uuid", nullable: true),
                    AutoNamed = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cards_Banks_BankId",
                        column: x => x.BankId,
                        principalTable: "Banks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CardIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Brand = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardIdentities_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_CardId",
                table: "Transactions",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TenantId_CardId",
                table: "Transactions",
                columns: new[] { "TenantId", "CardId" });

            migrationBuilder.CreateIndex(
                name: "IX_CardIdentities_CardId",
                table: "CardIdentities",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_CardIdentities_TenantId_Brand_Last4",
                table: "CardIdentities",
                columns: new[] { "TenantId", "Brand", "Last4" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cards_BankId",
                table: "Cards",
                column: "BankId");

            migrationBuilder.CreateIndex(
                name: "IX_Cards_TenantId_Name",
                table: "Cards",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_Cards_CardId",
                table: "Transactions",
                column: "CardId",
                principalTable: "Cards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Tenancy backstop (ADR-020): enable + force RLS and create the fail-closed policy on both tables.
            foreach (var table in Tables)
                foreach (var statement in RlsDdl.StatementsFor(table, "TenantId"))
                    migrationBuilder.Sql(statement);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"""DROP POLICY IF EXISTS {RlsDdl.PolicyName} ON "{table}";""");
                migrationBuilder.Sql($"""ALTER TABLE "{table}" NO FORCE ROW LEVEL SECURITY;""");
                migrationBuilder.Sql($"""ALTER TABLE "{table}" DISABLE ROW LEVEL SECURITY;""");
            }

            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Cards_CardId",
                table: "Transactions");

            migrationBuilder.DropTable(
                name: "CardIdentities");

            migrationBuilder.DropTable(
                name: "Cards");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_CardId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_TenantId_CardId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "CardId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "CardBrand",
                table: "PendingVouchers");
        }
    }
}
