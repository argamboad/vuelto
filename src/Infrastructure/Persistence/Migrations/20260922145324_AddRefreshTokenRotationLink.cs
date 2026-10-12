using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenRotationLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedByTokenId",
                table: "RefreshTokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RotatedAt",
                table: "RefreshTokens",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data loss on Down: RotatedAt and ReplacedByTokenId are discarded for every token (one-way). Accepted:
            // they only drive the reuse grace window, and the code this rollback returns to treats any revoked token
            // that is presented again as reuse — stricter, never looser. The token rows themselves survive.
            migrationBuilder.DropColumn(
                name: "ReplacedByTokenId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "RotatedAt",
                table: "RefreshTokens");
        }
    }
}
