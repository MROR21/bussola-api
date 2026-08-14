using Bussola.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bussola.Infrastructure.Data;

// Semeia os passos da jornada "Do Clone ao 1º Card" e o conteúdo (Markdown) de cada um.
// Idempotente: insere se a tabela estiver vazia; se já tiver passos, faz backfill do conteúdo
// que estiver em branco (não apaga usuários nem progresso).
public static class OnboardingSeeder
{
    // Conteúdo (Markdown) por Order do passo. Rascunho inicial — a curadoria fina vem depois.
    private static readonly Dictionary<int, string> Conteudos = new()
    {
        [1] = """
        ## Bem-vindo à Agilean
        A Agilean faz software de gestão para a construção civil. Você entra num squad de produto
        e trabalha em ciclos curtos, com bastante autonomia.

        ## O que saber primeiro
        - **Princípios:** entrega de valor, autonomia com responsabilidade, feedback rápido.
        - **Produtos:** o portal web e os apps de apoio à obra.
        - **Liderança:** você tem um gestor direto — ele é seu primeiro ponto de contato.
        """,
        [2] = """
        ## Seu squad e os outros
        A tecnologia é dividida em squads. Cada um cuida de uma parte do produto.

        - **Mão de Obra (MdO):** custos e alocação de equipe na obra (seu squad).
        - **Quiz Quality:** inspeção e qualidade.
        - **Agilean (desktop):** o aplicativo de planejamento.

        Descubra quem são os integrantes e quem é o seu gestor — anote os nomes.
        """,
        [3] = """
        ## Ferramentas & acessos
        Deixe o básico pronto antes de mexer em código:

        1. Instale o **VS Code** (front) e o **Visual Studio** (back .NET).
        2. Configure seu **e-mail Agilean**.
        3. Confirme acesso a **Jira** (cards), **Bitbucket** (código) e **Teams** (comunicação).

        > Bitbucket e Jira usam a mesma conta Atlassian. Se faltar acesso, fale com o gestor.
        """,
        [4] = """
        ## Entenda os repositórios
        O sistema é multi-repositório:

        - **agilean_portal** — o front (React + TypeScript + Vite).
        - **api** — o back (C# / .NET).
        - **projects / contract** — entram como **submódulos** do api.

        O `api` aponta para um commit específico de cada submódulo — por isso "bump de submódulo"
        aparece no fluxo de git mais pra frente.
        """,
        [5] = """
        ## Clone os repositórios
        Os repos do seu squad têm submódulos, então clone com `--recurse-submodules`:

        ```bash
        git clone --recurse-submodules <url-do-repo>
        ```

        Se você já clonou sem os submódulos:

        ```bash
        git submodule update --init --recursive
        ```
        """,
        [6] = """
        ## Suba o ambiente
        Com os repos clonados, instale as dependências e rode:

        - **Front:** `npm install` e depois `npm run dev` (Vite abre em localhost).
        - **Back:** abra a solution no Visual Studio e rode (perfil HTTP/Kestrel), ou `dotnet run`.

        Confirme que o front conversa com o back antes de seguir.
        """,
        [7] = """
        ## Padrões de código
        Tudo importante mora no `CLAUDE.md` de cada repo. O essencial do front:

        - Estilize com tokens **`ads-*`** (Tailwind) — **nunca** hex hardcoded nem CSS custom.
        - Todo elemento relevante recebe **`data-cy`** (`modulo-componente-elemento-tipo`).
        - Reaproveite o **Design System** antes de criar componente do zero.
        - Máximo **~400 linhas** por arquivo.
        """,
        [8] = """
        ## Fluxo git multi-repo
        O ciclo de um card, na ordem:

        1. Crie a branch a partir do **support**.
        2. Implemente e commite (`feat:` para melhoria, `fix:` para bug).
        3. **Rebase** no `support` antes de subir.
        4. Se mexeu num submódulo, faça o **bump** no `api` (aponta pro novo commit).
        5. `git push --force-with-lease` (nunca `--force` puro).
        """,
        [9] = """
        ## Jira & Bitbucket na prática
        - **Jira:** pegue o card, mova para "Em andamento", documente ao terminar.
        - **Bitbucket:** abra o **PR** com título e descrição claros e marque o **reviewer** da semana.
        - Responda os comentários do review no próprio PR.

        > Só mova o card para "pronto para teste" **depois** do merge, não na abertura do PR.
        """,
        [10] = """
        ## Pegue um card starter
        Seu primeiro card deve ser pequeno e de baixo risco — um *good-first-issue*.

        Exemplo clássico: **remover a tag de "beta"** de funcionalidades que já saíram de beta.
        O objetivo aqui é rodar o fluxo inteiro, não resolver algo difícil.
        """,
        [11] = """
        ## Crie a branch
        Padrão de nome, a partir do `support`:

        ```bash
        git checkout support
        git pull
        git checkout -b fix/MDO-XXX-support
        ```

        Troque `MDO-XXX` pelo código do seu card no Jira.
        """,
        [12] = """
        ## Implemente seguindo os padrões
        Agora o código, respeitando o `CLAUDE.md`:

        - Front e/ou back conforme o card.
        - Reaproveite componentes do Design System.
        - Adicione `data-cy` no que for interativo.
        - Commits pequenos e com mensagem clara.
        """,
        [13] = """
        ## Rode o gate
        Antes de abrir o PR, garanta que passa o mesmo gate do CI:

        ```bash
        npm run lint -- --max-warnings=0
        npm run build
        ```

        No front, **warning conta como erro** (`--max-warnings=0`). Só siga com tudo verde.
        """,
        [14] = """
        ## Abra o PR
        No Bitbucket:

        - **Título:** claro, no padrão do commit.
        - **Descrição:** o que muda e por quê.
        - **Reviewer:** o reviewer da semana.

        Não coloque nada de placeholder — o PR é real.
        """,
        [15] = """
        ## Review & ajustes
        O reviewer vai comentar. É normal e faz parte:

        - Responda cada comentário no PR.
        - Ajuste o que fizer sentido e faça push (a mesma branch atualiza o PR).
        - Se discordar, explique com educação — review é conversa.
        """,
        [16] = """
        ## Merge + documente
        Reta final:

        1. Com o PR aprovado, faça o **merge**.
        2. **Documente no Jira** e faça a transição do card.
        3. Comemore — **primeiro card entregue!** 🏆

        A partir daqui, é repetir o ciclo com cards cada vez mais robustos.
        """,
    };

    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.OnboardingSteps.AnyAsync())
        {
            // Backfill: preenche o conteúdo dos passos que ainda estão sem (bancos anteriores ao campo).
            var existentes = await db.OnboardingSteps.ToListAsync();
            var alterou = false;
            foreach (var step in existentes)
            {
                if (string.IsNullOrWhiteSpace(step.Conteudo) && Conteudos.TryGetValue(step.Order, out var md))
                {
                    step.Conteudo = md;
                    alterou = true;
                }
            }
            if (alterou)
            {
                await db.SaveChangesAsync();
            }
            return;
        }

        // As Fases já foram semeadas pela migration (SeedFaseAndModuloData) — aqui só referenciamos
        // pelo nome, nunca criamos Fase por conta própria (isso é papel do admin agora).
        var fasePorNome = await db.Fases.ToDictionaryAsync(f => f.Nome, f => f.Id);
        Guid IdDaFase(string nome) => fasePorNome[nome];

        db.OnboardingSteps.AddRange(
            // Fase A — Ambientação
            new OnboardingStep { Order = 1, FaseId = IdDaFase("Ambientação"), Title = "Bem-vindo à Agilean", Description = "O que é a empresa, princípios, líderes e os produtos.", IsCompanySpecific = true, Conteudo = Conteudos[1] },
            new OnboardingStep { Order = 2, FaseId = IdDaFase("Ambientação"), Title = "Seu squad e os outros", Description = "Mão de Obra, Quiz Quality e Agilean (desktop) — o que cada um faz, integrantes e seu gestor.", IsCompanySpecific = true, Conteudo = Conteudos[2] },
            new OnboardingStep { Order = 3, FaseId = IdDaFase("Ambientação"), Title = "Ferramentas & acessos", Description = "Instale VS Code e Visual Studio; configure o e-mail Agilean; confirme acesso ao Jira, Bitbucket e Teams.", IsCompanySpecific = true, Conteudo = Conteudos[3] },

            // Fase B — Ambiente técnico
            new OnboardingStep { Order = 4, FaseId = IdDaFase("Ambiente técnico"), Title = "Entenda os repositórios", Description = "agilean_portal (front), api (back), projects/contract (submódulos) e como se conectam.", IsCompanySpecific = true, Conteudo = Conteudos[4] },
            new OnboardingStep { Order = 5, FaseId = IdDaFase("Ambiente técnico"), Title = "Clone os repositórios", Description = "git clone --recurse-submodules dos repos do seu squad.", IsCompanySpecific = false, SkillArea = SkillArea.Git, Conteudo = Conteudos[5] },
            new OnboardingStep { Order = 6, FaseId = IdDaFase("Ambiente técnico"), Title = "Suba o ambiente", Description = "Instale as dependências e rode o front (Vite) e o back (dotnet).", IsCompanySpecific = false, Conteudo = Conteudos[6] },

            // Fase C — Padrões
            new OnboardingStep { Order = 7, FaseId = IdDaFase("Padrões"), Title = "Padrões de código", Description = "CLAUDE.md: tokens ads-*, data-cy, sem CSS custom, máx 400 linhas, reusar o Design System.", IsCompanySpecific = true, Conteudo = Conteudos[7] },
            new OnboardingStep { Order = 8, FaseId = IdDaFase("Padrões"), Title = "Fluxo git multi-repo", Description = "Branch por card → rebase no support → bump do submódulo no api → force-with-lease.", IsCompanySpecific = true, SkillArea = SkillArea.Git, Conteudo = Conteudos[8] },
            new OnboardingStep { Order = 9, FaseId = IdDaFase("Padrões"), Title = "Jira & Bitbucket na prática", Description = "Pegar card, transições, abrir PR, review e reviewer.", IsCompanySpecific = true, Conteudo = Conteudos[9] },

            // Fase D — Primeiro Card
            new OnboardingStep { Order = 10, FaseId = IdDaFase("Primeiro Card"), Title = "Pegue um card starter", Description = "Um good-first-issue simples (ex.: tirar a tag de beta de funcionalidades que não são mais beta).", IsCompanySpecific = true, Conteudo = Conteudos[10] },
            new OnboardingStep { Order = 11, FaseId = IdDaFase("Primeiro Card"), Title = "Crie a branch", Description = "fix/MDO-X-support a partir do support.", IsCompanySpecific = false, SkillArea = SkillArea.Git, Conteudo = Conteudos[11] },
            new OnboardingStep { Order = 12, FaseId = IdDaFase("Primeiro Card"), Title = "Implemente seguindo os padrões", Description = "Front e/ou back, respeitando o CLAUDE.md.", IsCompanySpecific = false, Conteudo = Conteudos[12] },
            new OnboardingStep { Order = 13, FaseId = IdDaFase("Primeiro Card"), Title = "Rode o gate", Description = "npm run lint --max-warnings=0 + build.", IsCompanySpecific = true, Conteudo = Conteudos[13] },
            new OnboardingStep { Order = 14, FaseId = IdDaFase("Primeiro Card"), Title = "Abra o PR", Description = "Título, descrição e o reviewer da semana.", IsCompanySpecific = true, Conteudo = Conteudos[14] },
            new OnboardingStep { Order = 15, FaseId = IdDaFase("Primeiro Card"), Title = "Review & ajustes", Description = "Responda os comentários e ajuste o que for pedido.", IsCompanySpecific = false, Conteudo = Conteudos[15] },
            new OnboardingStep { Order = 16, FaseId = IdDaFase("Primeiro Card"), Title = "Merge + documente", Description = "Mergeie, documente no Jira e faça a transição do card. Primeiro card entregue!", IsCompanySpecific = true, Conteudo = Conteudos[16] }
        );

        await db.SaveChangesAsync();
    }
}
