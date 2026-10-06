using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenSessionExpiresAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SessionExpiresAt",
                table: "RefreshTokens",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data loss on Down: SessionExpiresAt is discarded (one-way). Accepted: the code this rollback returns to
            // bounds a session by each token's own ExpiresAt only. The token rows themselves survive.
            migrationBuilder.DropColumn(
                name: "SessionExpiresAt",
                table: "RefreshTokens");
        }
    }
}
