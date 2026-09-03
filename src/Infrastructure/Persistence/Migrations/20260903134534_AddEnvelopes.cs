using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ENV-1 (app slice P4, ADR-V007/V008): the household's savings envelopes. <c>ITenantScoped</c>, so
    /// its RLS policy ships in this migration (ADR-020; add-a-slice step 4), generated from
    /// <see cref="RlsDdl"/> — the <c>RlsMigrationGateTests</c> parity gate fails CI otherwise.
    /// </summary>
    public partial class AddEnvelopes : Migration
    {
        private const string Table = "Envelopes";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Envelopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AnnualTargetCrc = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    AnnualTargetUsd = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    ReminderCadence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Envelopes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Envelopes_TenantId_Name",
                table: "Envelopes",
                columns: new[] { "TenantId", "Name" },
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
                name: "Envelopes");
        }
    }
}
