using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundReceivedDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ReceivedDate",
                table: "Refunds",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceivedDate",
                table: "Refunds");
        }
    }
}
