using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    AccessToken = table.Column<string>(type: "text", nullable: false),
                    RefreshToken = table.Column<string>(type: "text", nullable: false),
                    TokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Folders = table.Column<string[]>(type: "text[]", nullable: false),
                    SenderFilters = table.Column<string[]>(type: "text[]", nullable: false),
                    SubjectFilters = table.Column<string[]>(type: "text[]", nullable: false),
                    UnreadOnly = table.Column<bool>(type: "boolean", nullable: false),
                    ImportFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PollingIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    IgnoreCursor = table.Column<bool>(type: "boolean", nullable: false),
                    LastPolledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailConnections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailConnections_UserId_Provider",
                table: "EmailConnections",
                columns: new[] { "UserId", "Provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailConnections");
        }
    }
}
