using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropExpenseLineBank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FixedExpenses_Banks_BankId",
                table: "FixedExpenses");

            migrationBuilder.DropForeignKey(
                name: "FK_VariableExpenses_Banks_BankId",
                table: "VariableExpenses");

            migrationBuilder.DropIndex(
                name: "IX_VariableExpenses_BankId",
                table: "VariableExpenses");

            migrationBuilder.DropIndex(
                name: "IX_FixedExpenses_BankId",
                table: "FixedExpenses");

            migrationBuilder.DropColumn(
                name: "BankId",
                table: "VariableExpenses");

            migrationBuilder.DropColumn(
                name: "BankId",
                table: "FixedExpenses");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BankId",
                table: "VariableExpenses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BankId",
                table: "FixedExpenses",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VariableExpenses_BankId",
                table: "VariableExpenses",
                column: "BankId");

            migrationBuilder.CreateIndex(
                name: "IX_FixedExpenses_BankId",
                table: "FixedExpenses",
                column: "BankId");

            migrationBuilder.AddForeignKey(
                name: "FK_FixedExpenses_Banks_BankId",
                table: "FixedExpenses",
                column: "BankId",
                principalTable: "Banks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_VariableExpenses_Banks_BankId",
                table: "VariableExpenses",
                column: "BankId",
                principalTable: "Banks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
