using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Perezosoft.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditEventImpersonatedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ImpersonatedBy",
                table: "AuditEvents",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImpersonatedBy",
                table: "AuditEvents");
        }
    }
}
