using Bussola.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bussola.Infrastructure.Data;

// Semeia os passos da jornada "Do Clone ao 1º Card". Idempotente: só insere se a tabela estiver vazia.
public static class OnboardingSeeder
{
    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.OnboardingSteps.AnyAsync())
            return;

        db.OnboardingSteps.AddRange(
            // Fase A — Ambientação
            new OnboardingStep { Order = 1, Phase = "Ambientação", Title = "Bem-vindo à Agilean", Description = "O que é a empresa, princípios, líderes e os produtos.", IsCompanySpecific = true },
            new OnboardingStep { Order = 2, Phase = "Ambientação", Title = "Seu squad e os outros", Description = "Mão de Obra, Quiz Quality e Agilean (desktop) — o que cada um faz, integrantes e seu gestor.", IsCompanySpecific = true },
            new OnboardingStep { Order = 3, Phase = "Ambientação", Title = "Ferramentas & acessos", Description = "Instale VS Code e Visual Studio; configure o e-mail Agilean; confirme acesso ao Jira, Bitbucket e Teams.", IsCompanySpecific = true },

            // Fase B — Ambiente técnico
            new OnboardingStep { Order = 4, Phase = "Ambiente técnico", Title = "Entenda os repositórios", Description = "agilean_portal (front), api (back), projects/contract (submódulos) e como se conectam.", IsCompanySpecific = true },
            new OnboardingStep { Order = 5, Phase = "Ambiente técnico", Title = "Clone os repositórios", Description = "git clone --recurse-submodules dos repos do seu squad.", IsCompanySpecific = false, SkillArea = SkillArea.Git },
            new OnboardingStep { Order = 6, Phase = "Ambiente técnico", Title = "Suba o ambiente", Description = "Instale as dependências e rode o front (Vite) e o back (dotnet).", IsCompanySpecific = false },

            // Fase C — Padrões
            new OnboardingStep { Order = 7, Phase = "Padrões", Title = "Padrões de código", Description = "CLAUDE.md: tokens ads-*, data-cy, sem CSS custom, máx 400 linhas, reusar o Design System.", IsCompanySpecific = true },
            new OnboardingStep { Order = 8, Phase = "Padrões", Title = "Fluxo git multi-repo", Description = "Branch por card → rebase no support → bump do submódulo no api → force-with-lease.", IsCompanySpecific = true, SkillArea = SkillArea.Git },
            new OnboardingStep { Order = 9, Phase = "Padrões", Title = "Jira & Bitbucket na prática", Description = "Pegar card, transições, abrir PR, review e reviewer.", IsCompanySpecific = true },

            // Fase D — Primeiro Card
            new OnboardingStep { Order = 10, Phase = "Primeiro Card", Title = "Pegue um card starter", Description = "Um good-first-issue simples (ex.: tirar a tag de beta de funcionalidades que não são mais beta).", IsCompanySpecific = true },
            new OnboardingStep { Order = 11, Phase = "Primeiro Card", Title = "Crie a branch", Description = "fix/MDO-X-support a partir do support.", IsCompanySpecific = false, SkillArea = SkillArea.Git },
            new OnboardingStep { Order = 12, Phase = "Primeiro Card", Title = "Implemente seguindo os padrões", Description = "Front e/ou back, respeitando o CLAUDE.md.", IsCompanySpecific = false },
            new OnboardingStep { Order = 13, Phase = "Primeiro Card", Title = "Rode o gate", Description = "npm run lint --max-warnings=0 + build.", IsCompanySpecific = true },
            new OnboardingStep { Order = 14, Phase = "Primeiro Card", Title = "Abra o PR", Description = "Título, descrição e o reviewer da semana.", IsCompanySpecific = true },
            new OnboardingStep { Order = 15, Phase = "Primeiro Card", Title = "Review & ajustes", Description = "Responda os comentários e ajuste o que for pedido.", IsCompanySpecific = false },
            new OnboardingStep { Order = 16, Phase = "Primeiro Card", Title = "Merge + documente", Description = "Mergeie, documente no Jira e faça a transição do card. Primeiro card entregue!", IsCompanySpecific = true }
        );

        await db.SaveChangesAsync();
    }
}
