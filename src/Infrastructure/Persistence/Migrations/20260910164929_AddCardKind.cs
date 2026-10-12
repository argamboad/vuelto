using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCardKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CardKind",
                table: "PendingVouchers",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Cards",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "credit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CardKind",
                table: "PendingVouchers");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Cards");
        }
    }
}
