using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bussola.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApiTokenExpiraEm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiraEm",
                table: "ApiTokens",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiraEm",
                table: "ApiTokens");
        }
    }
}
