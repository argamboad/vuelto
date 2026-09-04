using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// EMAIL-5 (app slice P10b, ADR-V010): the household's merchant → category suggestion rules, unique per
    /// household on the lower-cased pattern key. <c>ITenantScoped</c>, so its RLS policy ships here (ADR-020),
    /// generated from <see cref="RlsDdl"/> — the <c>RlsMigrationGateTests</c> parity gate fails CI otherwise.
    /// </summary>
    public partial class AddMerchantCategoryMappings : Migration
    {
        private const string Table = "MerchantCategoryMappings";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MerchantCategoryMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantPattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PatternKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuggestedClass = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantCategoryMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MerchantCategoryMappings_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantCategoryMappings_CategoryId",
                table: "MerchantCategoryMappings",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantCategoryMappings_TenantId_PatternKey",
                table: "MerchantCategoryMappings",
                columns: new[] { "TenantId", "PatternKey" },
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
                name: "MerchantCategoryMappings");
        }
    }
}
