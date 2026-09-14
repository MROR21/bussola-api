using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bussola.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModuloIcone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Icone",
                table: "Modulos",
                type: "text",
                nullable: false,
                defaultValue: "inventory_2");

            // Backfill pros 4 módulos que já tinham ícone fixo no código (MODULO_ICONE em
            // GuiasPage.tsx, removido depois dessa migration) — qualquer outro módulo já existente
            // (ex.: um criado em teste) fica no default "inventory_2" acima, mesmo fallback de antes.
            migrationBuilder.Sql("""
                UPDATE "Modulos" SET "Icone" = 'engineering' WHERE "Nome" = 'Mão de Obra';
                UPDATE "Modulos" SET "Icone" = 'handyman' WHERE "Nome" = 'Básico do dev';
                UPDATE "Modulos" SET "Icone" = 'quiz' WHERE "Nome" = 'Quiz Quality';
                UPDATE "Modulos" SET "Icone" = 'desktop_windows' WHERE "Nome" = 'Agilean (desktop)';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Icone",
                table: "Modulos");
        }
    }
}
