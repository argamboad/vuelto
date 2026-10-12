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
            // Data loss on Down: the buy/sell pair and its date staged with each pending voucher are discarded (one-way).
            // Accepted: the code this rollback returns to books a confirmed voucher at the rate of the day it is
            // confirmed, which is what a null staged rate means today. The drafts themselves survive.
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
