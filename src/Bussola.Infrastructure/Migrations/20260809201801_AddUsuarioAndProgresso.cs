using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bussola.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUsuarioAndProgresso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Usuarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nome = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Cargo = table.Column<int>(type: "integer", nullable: false),
                    Frontend = table.Column<int>(type: "integer", nullable: false),
                    Backend = table.Column<int>(type: "integer", nullable: false),
                    Git = table.Column<int>(type: "integer", nullable: false),
                    Sql = table.Column<int>(type: "integer", nullable: false),
                    Jira = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usuarios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PassosConcluidos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    OnboardingStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConcluidoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PassosConcluidos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PassosConcluidos_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PassosConcluidos_UsuarioId_OnboardingStepId",
                table: "PassosConcluidos",
                columns: new[] { "UsuarioId", "OnboardingStepId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_Email",
                table: "Usuarios",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PassosConcluidos");

            migrationBuilder.DropTable(
                name: "Usuarios");
        }
    }
}
