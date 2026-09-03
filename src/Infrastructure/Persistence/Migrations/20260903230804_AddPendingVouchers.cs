using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// EMAIL-4 (app slice P10a, ADR-V010): the staged review drafts and their dedup tombstones. Both are
    /// <c>ITenantScoped</c>, so their RLS policies ship in this migration (ADR-020), generated from
    /// <see cref="RlsDdl"/> — the <c>RlsMigrationGateTests</c> parity gate fails CI otherwise.
    /// </summary>
    public partial class AddPendingVouchers : Migration
    {
        private static readonly string[] Tables = ["PendingVouchers", "IngestedVouchers"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IngestedVouchers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PendingVoucherId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestedVouchers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingVouchers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ParsedBank = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BankId = table.Column<Guid>(type: "uuid", nullable: true),
                    Merchant = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: true),
                    CardNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Authorization = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TransactionType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    MissingFields = table.Column<string[]>(type: "text[]", nullable: false),
                    SuggestedCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    SuggestedClass = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConfirmedTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingVouchers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingVouchers_Banks_BankId",
                        column: x => x.BankId,
                        principalTable: "Banks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingVouchers_Categories_SuggestedCategoryId",
                        column: x => x.SuggestedCategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IngestedVouchers_TenantId_Fingerprint",
                table: "IngestedVouchers",
                columns: new[] { "TenantId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingVouchers_BankId",
                table: "PendingVouchers",
                column: "BankId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingVouchers_SuggestedCategoryId",
                table: "PendingVouchers",
                column: "SuggestedCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingVouchers_TenantId_Status",
                table: "PendingVouchers",
                columns: new[] { "TenantId", "Status" });

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

            migrationBuilder.DropTable(
                name: "IngestedVouchers");

            migrationBuilder.DropTable(
                name: "PendingVouchers");
        }
    }
}
