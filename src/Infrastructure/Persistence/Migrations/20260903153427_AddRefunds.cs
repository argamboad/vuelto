using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// LEDGER-3 (app slice P5b, ADR-V007/V014): expected refunds derived from unplanned-essential
    /// transactions. <c>ITenantScoped</c>, so its RLS policy ships here (ADR-020; add-a-slice step 4),
    /// generated from <see cref="RlsDdl"/> — the <c>RlsMigrationGateTests</c> parity gate fails CI otherwise.
    /// The source-transaction FK cascades (one refund per transaction); the realized-inflow FK is SET NULL.
    /// </summary>
    public partial class AddRefunds : Migration
    {
        private const string Table = "Refunds";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Refunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MonthId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Payee = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TransactionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    AmountCrc = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    InflowTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Refunds_Months_MonthId",
                        column: x => x.MonthId,
                        principalTable: "Months",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Refunds_Transactions_InflowTransactionId",
                        column: x => x.InflowTransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Refunds_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_InflowTransactionId",
                table: "Refunds",
                column: "InflowTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_MonthId",
                table: "Refunds",
                column: "MonthId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_TenantId_MonthId",
                table: "Refunds",
                columns: new[] { "TenantId", "MonthId" });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_TransactionId",
                table: "Refunds",
                column: "TransactionId",
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
                name: "Refunds");
        }
    }
}
