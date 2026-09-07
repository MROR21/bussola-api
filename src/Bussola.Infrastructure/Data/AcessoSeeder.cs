using Bussola.Domain.Entities;
using Bussola.Domain.Nivelamento;
using Microsoft.EntityFrameworkCore;

namespace Bussola.Infrastructure.Data;

// Semeia os Acessos com a mesma lista ilustrativa que antes vivia hardcoded no front
// (ACESSOS_POR_CARGO) — só a base pra o admin editar/completar pela tela de Admin > Acessos daqui
// pra frente. Idempotente: só insere se a tabela estiver vazia (não sobrescreve edição do admin).
public static class AcessoSeeder
{
    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.Acessos.AnyAsync()) return;

        var acessos = new List<Acesso>
        {
            new() { Order = 1, CargoMinimo = Cargo.Estagiario, Nome = "E-mail Agilean", Link = "" },
            new() { Order = 2, CargoMinimo = Cargo.Estagiario, Nome = "Teams", Link = "" },
            new() { Order = 3, CargoMinimo = Cargo.Estagiario, Nome = "Agilean Flow", Link = "" },
            new() { Order = 4, CargoMinimo = Cargo.Estagiario, Nome = "VS Code / Visual Studio", Link = "" },
            new() { Order = 5, CargoMinimo = Cargo.Estagiario, Nome = "Atlassian (Jira + Bitbucket)", Link = "" },
            new() { Order = 6, CargoMinimo = Cargo.Estagiario, Nome = "Bússola", Link = "" },
            new() { Order = 7, CargoMinimo = Cargo.Junior, Nome = "Zendesk", Link = "" },
            new() { Order = 8, CargoMinimo = Cargo.Pleno, Nome = "Azure DevOps", Link = "" },
        };

        db.Acessos.AddRange(acessos);
        await db.SaveChangesAsync();
    }
}
