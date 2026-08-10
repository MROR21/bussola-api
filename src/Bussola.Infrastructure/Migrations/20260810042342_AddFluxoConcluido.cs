using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bussola.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFluxoConcluido : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FluxosConcluidos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    FluxoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConcluidoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FluxosConcluidos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FluxosConcluidos_UsuarioId_FluxoId",
                table: "FluxosConcluidos",
                columns: new[] { "UsuarioId", "FluxoId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FluxosConcluidos");
        }
    }
}
