using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailConnectionFolderNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "FolderNames",
                table: "EmailConnections",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FolderNames",
                table: "EmailConnections");
        }
    }
}
