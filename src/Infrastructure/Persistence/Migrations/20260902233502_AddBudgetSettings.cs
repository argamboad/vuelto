using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// BUDGET-1 (app slice P1, ADR-V003): the household's budget structure — one row per tenant.
    /// The table is <c>ITenantScoped</c>, so its RLS policy ships here too (ADR-020 / WAYS_OF_WORKING
    /// add-a-slice step 4): <c>dotnet ef migrations add</c> scaffolds no RLS DDL, and the
    /// <c>RlsMigrationGateTests</c> parity gate fails CI for any tenant-scoped table whose policy did
    /// not arrive by migration. The statements come from <see cref="RlsDdl"/> so they can never drift
    /// from the platform's predicate.
    /// </summary>
    public partial class AddBudgetSettings : Migration
    {
        private const string Table = "BudgetSettings";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: Table,
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    WeekStartWeekday = table.Column<int>(type: "integer", nullable: false),
                    MonthAnchor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PrimaryIncome4w = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    PrimaryIncome5w = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    PrimaryIncomeCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SecondaryIncome4w = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    SecondaryIncome5w = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    SecondaryIncomeCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetSettings_TenantId",
                table: Table,
                column: "TenantId",
                unique: true);

            // Tenancy backstop (ADR-020): enable + force RLS and create the fail-closed policy.
            foreach (var statement in RlsDdl.StatementsFor(Table, "TenantId"))
                migrationBuilder.Sql(statement);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""DROP POLICY IF EXISTS {RlsDdl.PolicyName} ON "{Table}";""");
            migrationBuilder.Sql($"""ALTER TABLE "{Table}" NO FORCE ROW LEVEL SECURITY;""");
            migrationBuilder.Sql($"""ALTER TABLE "{Table}" DISABLE ROW LEVEL SECURITY;""");

            migrationBuilder.DropTable(
                name: Table);
        }
    }
}
