using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenGraceUsedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GraceUsedAt",
                table: "RefreshTokens",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data loss on Down: GraceUsedAt is discarded (one-way). Accepted: the code this rollback returns to has no
            // one-shot grace, so the stamp has no reader. The token rows themselves survive.
            migrationBuilder.DropColumn(
                name: "GraceUsedAt",
                table: "RefreshTokens");
        }
    }
}
