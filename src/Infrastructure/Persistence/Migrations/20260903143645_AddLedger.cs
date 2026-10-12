using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// LEDGER-1/2 (app slice P5a, ADR-V005/V007): budget months, their materialized weeks, and
    /// transactions. All three are <c>ITenantScoped</c>, so their RLS policies ship here (ADR-020;
    /// add-a-slice step 4), generated from <see cref="RlsDdl"/> — the <c>RlsMigrationGateTests</c>
    /// parity gate fails CI otherwise. Catalog FKs (bank, category, envelope) are RESTRICT: those
    /// parents are soft-deleted and must keep naming history; weeks and transactions cascade with
    /// their month.
    /// </summary>
    public partial class AddLedger : Migration
    {
        private static readonly string[] Tables = ["Months", "Weeks", "Transactions"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Months",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    MonthNumber = table.Column<int>(type: "integer", nullable: false),
                    WeekCount = table.Column<int>(type: "integer", nullable: false),
                    Week1StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PrimaryIncomeAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    PrimaryIncomeCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SecondaryIncomeAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    SecondaryIncomeCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Months", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MonthId = table.Column<Guid>(type: "uuid", nullable: false),
                    BankId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvelopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Payee = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OriginalAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    TransactionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountCrc = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    ExchangeRateUsed = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    TransactionType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transactions_Banks_BankId",
                        column: x => x.BankId,
                        principalTable: "Banks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Transactions_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Transactions_Envelopes_EnvelopeId",
                        column: x => x.EnvelopeId,
                        principalTable: "Envelopes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Transactions_Months_MonthId",
                        column: x => x.MonthId,
                        principalTable: "Months",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Weeks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MonthId = table.Column<Guid>(type: "uuid", nullable: false),
                    WeekNumber = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Weeks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Weeks_Months_MonthId",
                        column: x => x.MonthId,
                        principalTable: "Months",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Months_TenantId_Week1StartDate",
                table: "Months",
                columns: new[] { "TenantId", "Week1StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Months_TenantId_Year_MonthNumber",
                table: "Months",
                columns: new[] { "TenantId", "Year", "MonthNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_BankId",
                table: "Transactions",
                column: "BankId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_CategoryId",
                table: "Transactions",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_EnvelopeId",
                table: "Transactions",
                column: "EnvelopeId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_MonthId",
                table: "Transactions",
                column: "MonthId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TenantId_CreatedAt",
                table: "Transactions",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TenantId_MonthId",
                table: "Transactions",
                columns: new[] { "TenantId", "MonthId" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TenantId_TransactionDate",
                table: "Transactions",
                columns: new[] { "TenantId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Weeks_MonthId_WeekNumber",
                table: "Weeks",
                columns: new[] { "MonthId", "WeekNumber" },
                unique: true);

            // Tenancy backstop (ADR-020): enable + force RLS and create the fail-closed policy per table.
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

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "Weeks");

            migrationBuilder.DropTable(
                name: "Months");
        }
    }
}
