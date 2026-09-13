using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bussola.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Squad deixa de ser um enum fixo (3 valores hardcoded) e vira entidade própria — o admin
    // cria/renomeia/reordena pela tela. Hand-editada a partir do scaffold do EF: a ordem gerada
    // automaticamente dropava as colunas int ANTES de dar chance de fazer o backfill (perderia o
    // squad de todo mundo). Aqui a ordem é: cria coluna nova (nullable) → backfill via SQL a
    // partir da coluna int antiga (ainda presente) → só then dropa a antiga e aperta a nova pra
    // NOT NULL. Mesma ideia da migration SeedFaseAndModuloData, só que num arquivo só (a
    // "migration" de dado não precisa estar isolada num arquivo à parte pra rodar em segurança —
    // é tudo uma transação só de qualquer forma).
    public partial class AddSquadEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Squads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nome = table.Column<string>(type: "text", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Squads", x => x.Id);
                });

            migrationBuilder.AddColumn<Guid>(
                name: "SquadId",
                table: "Usuarios",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SquadId",
                table: "Modulos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SquadId",
                table: "Fluxos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Tipo",
                table: "Fluxos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Semeia os 3 squads (mesmos nomes já usados em PerfilPage.tsx/NivelamentoForm.tsx) e
            // faz o backfill a partir das colunas int antigas, que ainda existem nesse ponto —
            // 0=MaoDeObra, 1=QuizQuality, 2=Agilean (ordinais do enum original). Módulo é
            // vinculado por MATCH EXATO DE NOME contra os 3 squads (só "Mão de Obra"/"Quiz
            // Quality"/"Agilean (desktop)" — "Básico do dev" e qualquer outro módulo ficam com
            // SquadId NULL de propósito, preservando o comportamento visual de hoje). Guids
            // gerados em C# (não gen_random_uuid() do Postgres) — mesmo padrão já comprovado em
            // SeedFaseAndModuloData.
            var maoDeObra = Guid.NewGuid();
            var quizQuality = Guid.NewGuid();
            var agilean = Guid.NewGuid();

            migrationBuilder.Sql($@"
                INSERT INTO ""Squads"" (""Id"", ""Nome"", ""Order"") VALUES
                ('{maoDeObra}', 'Mão de Obra', 1),
                ('{quizQuality}', 'Quiz Quality', 2),
                ('{agilean}', 'Agilean (desktop)', 3);
            ");

            migrationBuilder.Sql($@"
                UPDATE ""Usuarios"" SET ""SquadId"" = CASE ""Squad""
                    WHEN 0 THEN '{maoDeObra}'::uuid
                    WHEN 1 THEN '{quizQuality}'::uuid
                    WHEN 2 THEN '{agilean}'::uuid
                END;
            ");

            migrationBuilder.Sql($@"
                UPDATE ""Fluxos"" SET ""SquadId"" = CASE ""Squad""
                    WHEN 0 THEN '{maoDeObra}'::uuid
                    WHEN 1 THEN '{quizQuality}'::uuid
                    WHEN 2 THEN '{agilean}'::uuid
                END;
            ");

            migrationBuilder.Sql(@"
                UPDATE ""Modulos"" m
                SET ""SquadId"" = s.""Id""
                FROM ""Squads"" s
                WHERE s.""Nome"" = m.""Nome"";
            ");

            migrationBuilder.DropColumn(
                name: "Squad",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "Squad",
                table: "Fluxos");

            migrationBuilder.AlterColumn<Guid>(
                name: "SquadId",
                table: "Usuarios",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_SquadId",
                table: "Usuarios",
                column: "SquadId");

            migrationBuilder.CreateIndex(
                name: "IX_Modulos_SquadId",
                table: "Modulos",
                column: "SquadId");

            migrationBuilder.CreateIndex(
                name: "IX_Fluxos_SquadId",
                table: "Fluxos",
                column: "SquadId");

            migrationBuilder.CreateIndex(
                name: "IX_Squads_Nome",
                table: "Squads",
                column: "Nome",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Fluxos_Squads_SquadId",
                table: "Fluxos",
                column: "SquadId",
                principalTable: "Squads",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Modulos_Squads_SquadId",
                table: "Modulos",
                column: "SquadId",
                principalTable: "Squads",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Usuarios_Squads_SquadId",
                table: "Usuarios",
                column: "SquadId",
                principalTable: "Squads",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Fluxos_Squads_SquadId",
                table: "Fluxos");

            migrationBuilder.DropForeignKey(
                name: "FK_Modulos_Squads_SquadId",
                table: "Modulos");

            migrationBuilder.DropForeignKey(
                name: "FK_Usuarios_Squads_SquadId",
                table: "Usuarios");

            migrationBuilder.DropTable(
                name: "Squads");

            migrationBuilder.DropIndex(
                name: "IX_Usuarios_SquadId",
                table: "Usuarios");

            migrationBuilder.DropIndex(
                name: "IX_Modulos_SquadId",
                table: "Modulos");

            migrationBuilder.DropIndex(
                name: "IX_Fluxos_SquadId",
                table: "Fluxos");

            migrationBuilder.DropColumn(
                name: "SquadId",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "SquadId",
                table: "Modulos");

            migrationBuilder.DropColumn(
                name: "SquadId",
                table: "Fluxos");

            migrationBuilder.DropColumn(
                name: "Tipo",
                table: "Fluxos");

            migrationBuilder.AddColumn<int>(
                name: "Squad",
                table: "Usuarios",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Squad",
                table: "Fluxos",
                type: "integer",
                nullable: true);
        }
    }
}
