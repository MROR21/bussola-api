using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bussola.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Migração de DADOS (sem mudança de modelo): semeia as Fases/Módulos a partir dos nomes que já
    // existiam como string solta (Phase/Modulo) e faz o backfill do FaseId/ModuloId de quem já
    // tinha linhas. Roda sempre — em banco vazio, os INSERTs criam as fases/módulos base e os
    // UPDATEs simplesmente não encontram linha nenhuma pra atualizar (harmless).
    public partial class SeedFaseAndModuloData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var ambientacao = Guid.NewGuid();
            var ambienteTecnico = Guid.NewGuid();
            var padroes = Guid.NewGuid();
            var primeiroCard = Guid.NewGuid();

            migrationBuilder.Sql($@"
                INSERT INTO ""Fases"" (""Id"", ""Nome"", ""Order"") VALUES
                ('{ambientacao}', 'Ambientação', 1),
                ('{ambienteTecnico}', 'Ambiente técnico', 2),
                ('{padroes}', 'Padrões', 3),
                ('{primeiroCard}', 'Primeiro Card', 4);
            ");

            migrationBuilder.Sql(@"
                UPDATE ""OnboardingSteps"" os
                SET ""FaseId"" = f.""Id""
                FROM ""Fases"" f
                WHERE f.""Nome"" = os.""Phase"" AND os.""FaseId"" IS NULL;
            ");

            var maoDeObra = Guid.NewGuid();
            var quizQuality = Guid.NewGuid();
            var agilean = Guid.NewGuid();
            var basicoDoDev = Guid.NewGuid();

            migrationBuilder.Sql($@"
                INSERT INTO ""Modulos"" (""Id"", ""Nome"", ""Order"") VALUES
                ('{maoDeObra}', 'Mão de Obra', 1),
                ('{quizQuality}', 'Quiz Quality', 2),
                ('{agilean}', 'Agilean (desktop)', 3),
                ('{basicoDoDev}', 'Básico do dev', 4);
            ");

            migrationBuilder.Sql(@"
                UPDATE ""Fluxos"" fl
                SET ""ModuloId"" = m.""Id""
                FROM ""Modulos"" m
                WHERE m.""Nome"" = fl.""Modulo"" AND fl.""ModuloId"" IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"UPDATE ""OnboardingSteps"" SET ""FaseId"" = NULL;");
            migrationBuilder.Sql(@"UPDATE ""Fluxos"" SET ""ModuloId"" = NULL;");
            migrationBuilder.Sql(@"DELETE FROM ""Fases"" WHERE ""Nome"" IN ('Ambientação', 'Ambiente técnico', 'Padrões', 'Primeiro Card');");
            migrationBuilder.Sql(@"DELETE FROM ""Modulos"" WHERE ""Nome"" IN ('Mão de Obra', 'Quiz Quality', 'Agilean (desktop)', 'Básico do dev');");
        }
    }
}
