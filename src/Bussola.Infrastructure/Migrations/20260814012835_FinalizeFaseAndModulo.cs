using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bussola.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FinalizeFaseAndModulo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Fluxos_Modulos_ModuloId",
                table: "Fluxos");

            migrationBuilder.DropForeignKey(
                name: "FK_OnboardingSteps_Fases_FaseId",
                table: "OnboardingSteps");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "OnboardingSteps");

            migrationBuilder.DropColumn(
                name: "Modulo",
                table: "Fluxos");

            migrationBuilder.AlterColumn<Guid>(
                name: "FaseId",
                table: "OnboardingSteps",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ModuloId",
                table: "Fluxos",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Modulos_Nome",
                table: "Modulos",
                column: "Nome",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Fases_Nome",
                table: "Fases",
                column: "Nome",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Fluxos_Modulos_ModuloId",
                table: "Fluxos",
                column: "ModuloId",
                principalTable: "Modulos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OnboardingSteps_Fases_FaseId",
                table: "OnboardingSteps",
                column: "FaseId",
                principalTable: "Fases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
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

            migrationBuilder.DropIndex(
                name: "IX_Modulos_Nome",
                table: "Modulos");

            migrationBuilder.DropIndex(
                name: "IX_Fases_Nome",
                table: "Fases");

            migrationBuilder.AlterColumn<Guid>(
                name: "FaseId",
                table: "OnboardingSteps",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Phase",
                table: "OnboardingSteps",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<Guid>(
                name: "ModuloId",
                table: "Fluxos",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Modulo",
                table: "Fluxos",
                type: "text",
                nullable: false,
                defaultValue: "");

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
    }
}
