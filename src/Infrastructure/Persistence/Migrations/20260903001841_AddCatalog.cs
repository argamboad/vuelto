using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// CATALOG-1/2 (app slice P2, ADR-V008): the household's categories and banks. Both tables are
    /// <c>ITenantScoped</c>, so their RLS policies ship in this migration (ADR-020; add-a-slice step 4),
    /// generated from <see cref="RlsDdl"/> — the <c>RlsMigrationGateTests</c> parity gate fails CI otherwise.
    /// </summary>
    public partial class AddCatalog : Migration
    {
        private static readonly string[] Tables = ["Banks", "Categories"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Banks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Banks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Banks_TenantId_Name",
                table: "Banks",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_TenantId_Name",
                table: "Categories",
                columns: new[] { "TenantId", "Name" },
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
                name: "Banks");

            migrationBuilder.DropTable(
                name: "Categories");
        }
    }
}
