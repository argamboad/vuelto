using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingVoucherStagedRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StagedRateAsOf",
                table: "PendingVouchers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StagedRateBuy",
                table: "PendingVouchers",
                type: "numeric(10,4)",
                precision: 10,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StagedRateSell",
                table: "PendingVouchers",
                type: "numeric(10,4)",
                precision: 10,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StagedRateAsOf",
                table: "PendingVouchers");

            migrationBuilder.DropColumn(
                name: "StagedRateBuy",
                table: "PendingVouchers");

            migrationBuilder.DropColumn(
                name: "StagedRateSell",
                table: "PendingVouchers");
        }
    }
}
