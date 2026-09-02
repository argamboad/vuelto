using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Perezosoft.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MfaLockout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedAttemptCount",
                table: "UserMfa",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntil",
                table: "UserMfa",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedAttemptCount",
                table: "UserMfa");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "UserMfa");
        }
    }
}
