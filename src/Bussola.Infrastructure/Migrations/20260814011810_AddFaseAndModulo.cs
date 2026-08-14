using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bussola.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFaseAndModulo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FaseId",
                table: "OnboardingSteps",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ModuloId",
                table: "Fluxos",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Fases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nome = table.Column<string>(type: "text", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Modulos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nome = table.Column<string>(type: "text", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Modulos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingSteps_FaseId",
                table: "OnboardingSteps",
                column: "FaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Fluxos_ModuloId",
                table: "Fluxos",
                column: "ModuloId");

            migrationBuilder.AddForeignKey(
                name: "FK_Fluxos_Modulos_ModuloId",
                table: "Fluxos",
                column: "ModuloId",
                principalTable: "Modulos",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_OnboardingSteps_Fases_FaseId",
                table: "OnboardingSteps",
                column: "FaseId",
                principalTable: "Fases",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Fluxos_Modulos_ModuloId",
                table: "Fluxos");

            migrationBuilder.DropForeignKey(
                name: "FK_OnboardingSteps_Fases_FaseId",
                table: "OnboardingSteps");

            migrationBuilder.DropTable(
                name: "Fases");

            migrationBuilder.DropTable(
                name: "Modulos");

            migrationBuilder.DropIndex(
                name: "IX_OnboardingSteps_FaseId",
                table: "OnboardingSteps");

            migrationBuilder.DropIndex(
                name: "IX_Fluxos_ModuloId",
                table: "Fluxos");

            migrationBuilder.DropColumn(
                name: "FaseId",
                table: "OnboardingSteps");

            migrationBuilder.DropColumn(
                name: "ModuloId",
                table: "Fluxos");
        }
    }
}
