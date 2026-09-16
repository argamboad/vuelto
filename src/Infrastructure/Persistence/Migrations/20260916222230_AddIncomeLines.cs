using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// INCOME-1 (ADR-V023): the household's income lines and each month's income rows. <b>Additive only</b> (plan §4a):
    /// it creates the two tables — with their RLS policies (ADR-020; the <c>RlsMigrationGateTests</c> parity gate) — and
    /// copies the old two-income data into them (<see cref="IncomeBackfill"/>: month amounts verbatim, idempotent, RLS
    /// bypass for this transaction). It drops, renames and rewrites nothing: the old <c>BudgetSettings</c> / <c>Months</c>
    /// income columns stay as the rollback baseline until the owner-gated INCOME-3. <c>Down</c> removes only the new tables.
    /// </summary>
    public partial class AddIncomeLines : Migration
    {
        private static readonly string[] Tables = ["IncomeLines", "MonthIncomes"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IncomeLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MemberUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PayPeriod = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    PayDay1 = table.Column<int>(type: "integer", nullable: true),
                    PayDay2 = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncomeLines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MonthIncomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MonthId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncomeLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MemberUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    PlannedAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthIncomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonthIncomes_IncomeLines_IncomeLineId",
                        column: x => x.IncomeLineId,
                        principalTable: "IncomeLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MonthIncomes_Months_MonthId",
                        column: x => x.MonthId,
                        principalTable: "Months",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IncomeLines_TenantId_Name",
                table: "IncomeLines",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncomeLines_TenantId_SortOrder",
                table: "IncomeLines",
                columns: new[] { "TenantId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_MonthIncomes_IncomeLineId",
                table: "MonthIncomes",
                column: "IncomeLineId");

            migrationBuilder.CreateIndex(
                name: "IX_MonthIncomes_MonthId",
                table: "MonthIncomes",
                column: "MonthId");

            migrationBuilder.CreateIndex(
                name: "IX_MonthIncomes_TenantId_MonthId_SortOrder",
                table: "MonthIncomes",
                columns: new[] { "TenantId", "MonthId", "SortOrder" });

            // Tenancy backstop (ADR-020): enable + force RLS and create the fail-closed policy on both tables.
            foreach (var table in Tables)
                foreach (var statement in RlsDdl.StatementsFor(table, "TenantId"))
                    migrationBuilder.Sql(statement);

            // Copy the old two-income data (settings defaults → lines, month slots → rows). Inside this migration's transaction.
            migrationBuilder.Sql(IncomeBackfill.Sql);
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

            migrationBuilder.DropTable(
                name: "MonthIncomes");

            migrationBuilder.DropTable(
                name: "IncomeLines");
        }
    }
}
