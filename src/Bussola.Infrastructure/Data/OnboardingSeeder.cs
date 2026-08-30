using Bussola.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bussola.Infrastructure.Data;

// Semeia os passos da jornada "Do Clone ao 1º Card" e o conteúdo (Markdown) de cada um.
// Idempotente: insere se a tabela estiver vazia; se já tiver passos, faz backfill do conteúdo
// que estiver em branco (não apaga usuários nem progresso).
public static class OnboardingSeeder
{
    // Conteúdo (Markdown) por Order do passo. Ambientação (1-7) usa o material real do RH
    // (Onboarding Agilean, PDF); o resto é rascunho técnico do squad.
    private static readonly Dictionary<int, string> Conteudos = new()
    {
        [1] = """
        ## Bem-vindo à Agilean
        Hey, novo Agilean lover! Sua chegada aqui vai ser leve, acolhedora e ágil — a gente já
        reuniu tudo o que você precisa saber pra começar com o pé direito.

        ## Sobre a empresa
        A **Agilean** é uma empresa de tecnologia voltada para a construção civil, que nasceu com o
        propósito de transformar a gestão de obras por meio da digitalização e da filosofia **Lean**.
        Temos clientes em 22 estados, mas é em **Fortaleza** que a mágica acontece — é aqui que
        construímos, todo dia, soluções pra aumentar produtividade, reduzir desperdício e apoiar
        construtoras na entrega de obras mais eficientes.

        ## Missão
        Tornar a Construção Civil uma indústria de excelência em gestão e produtividade, através da
        nossa equipe, processos e tecnologias inovadoras.

        ## Visão
        Ser a plataforma protagonista no movimento de digitalização da construção, tornando a gestão
        de obras inteligente, conectada e autônoma — impactando 3.500 canteiros simultâneos até o
        fim de 2027.

        > O próprio criador do Lean Construction, Lauri Koskela, já parabenizou a Agilean pelo nível
        > de Lean Construction que a plataforma possibilita para as empresas.
        """,
        [2] = """
        ## Nossa história
        - **Mar 2017** — Fundação da Aval Tech.
        - **Out 2018** — Lançamento do Agilean.
        - **Jul 2019** — Entrega do Vetor Ag.
        - **Out 2019** — Prêmio Nutec.
        - **Ago 2020** — Chegada em 100 obras.
        - **Dez 2020** — Prêmio CBIC Inovação.
        - **Set 2021** — Round 1 com a Arcelor Mittal.

        ## Nossos produtos
        - **Planejamento & Controle** — planejamento, medições, gestão de resultados e comunicação
          com o canteiro.
        - **Qualidade** — FVS, FVM, rastreabilidade tecnológica, saúde do trabalho, planos de ação.
        - **Mão de Obra** — gestão de equipe própria e terceirizada: medição de contratos,
          produtividade, folha de produção. *(é o produto do seu squad!)*

        ## Nossas soluções
        Pensadas pra cada fase de evolução do modelo de gestão Lean do cliente: **Essencial**
        (primeiros passos), **Escala** (amplia pra todos os canteiros), **Maestria** (qualidade
        integrada ao fluxo) e **Completo** (produtividade máxima, os 3 produtos juntos).
        """,
        [3] = """
        ## Os valores do nosso time
        - **Time campeão e integrado**
        - **Só fazemos com qualidade**
        - **Paixão por resultados**
        - **Só é bom para a Agilean, se for bom para o cliente**
        - **Compromisso com tudo o que fazemos**

        ## Comportamentos esperados na Agilean
        - Agimos com **energia e foco**, acompanhamos metas e buscamos gerar impacto real com
          nossas entregas.
        - Colaboramos de forma **ativa**, compartilhando conhecimento, apoiando uns aos outros e
          celebrando conquistas em equipe.
        - Entendemos a real necessidade do **cliente** e entregamos soluções que geram valor.
        - Entregamos com **excelência**, revisamos o que produzimos e buscamos melhoria contínua em
          cada detalhe.
        - Cumprimos o que **prometemos**, assumimos responsabilidades e mantemos consistência mesmo
          diante dos desafios.
        """,
        [4] = """
        ## Liderança
        - **André Quinderé** — CEO e Diretor Comercial
        - **Juliana Quinderé** — Diretora de CS/Adm/Financeiro
        - **Lucas Timbó** — Diretor de Tecnologia
        - **Igor Araújo** — CTO Interino
        - **Gabriel Soares** — Gerente de CS
        - **Israel Chacon** — Gerente Comercial

        ## Seu squad e os outros
        A tecnologia é dividida em squads. Cada um cuida de uma parte do produto.

        - **Mão de Obra (MdO):** custos e alocação de equipe na obra (seu squad).
        - **Quiz Quality:** inspeção e qualidade.
        - **Agilean (desktop):** o aplicativo de planejamento.

        Descubra quem é o seu gestor direto dentro do squad — anote o nome.
        """,
        [5] = """
        ## Ferramentas de comunicação
        - **Feedz** — avaliações de desempenho e comunicação de RH.
        - **Microsoft Teams** — comunicação do dia a dia, reuniões e chamadas.

        ## Rituais da liderança
        - **Reunião de Liderança** — toda segunda-feira de manhã, foco em revisão de OKRs, metas e
          planos de ação.
        - **Encontro entre lideranças** — alinhamentos estratégicos e tomada de decisão.
        - **Conecta (mensal)** — apresentação de resultados e comunicação para a empresa inteira.

        ## Período de experiência
        Acompanhamento em dois momentos — **45 dias** e **90 dias** — pela plataforma Feedz, no
        formato **180º** (feedback do líder + autoavaliação sua).
        """,
        [6] = """
        ## Ferramentas & acessos
        Deixe o básico pronto antes de mexer em código:

        1. Instale o **VS Code** (front) e o **Visual Studio** (back .NET).
        2. Configure seu **e-mail Agilean**.
        3. Confirme acesso a **Jira** (cards), **Bitbucket** (código) e **Teams** (comunicação).

        > Bitbucket e Jira usam a mesma conta Atlassian. Se faltar acesso, fale com o gestor.
        """,
        [7] = """
        ## Pagamento
        - **CLT** — todo dia 05.
        - **PJ** — todo dia 05.
        - **Flash** (benefício) — disponível já no seu dia de entrada: primeiro entram 10 dias
          úteis, depois o restante do mês.

        ## Plano de saúde
        Dois planos disponíveis — **Hapvida** (a empresa paga 50%) e **Amil** (a empresa paga 30%).
        O valor varia com a idade; dependente paga o valor integral. Benefício disponível para CLT
        e PJ.

        ## Treinamentos
        - **Intranet** — onde fica a documentação e as políticas internas.
        - **Flow** — a plataforma de gestão interna da Agilean.

        ## Precisa falar com o RH?
        - **Aline Vasconcelos** — (85) 99690-0185
        - **Jéssica Veloso** — (85) 99708-5121
        - **Isabella Montenegro** — (85) 98500-7749
        """,
        [8] = """
        ## Entenda os repositórios
        O sistema é multi-repositório:

        - **agilean_portal** — o front (React + TypeScript + Vite).
        - **api** — o back (C# / .NET).
        - **projects / contract** — entram como **submódulos** do api.

        O `api` aponta para um commit específico de cada submódulo — por isso "bump de submódulo"
        aparece no fluxo de git mais pra frente.
        """,
        [9] = """
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
        [10] = """
        ## Suba o ambiente
        Com os repos clonados, instale as dependências e rode:

        - **Front:** `npm install` e depois `npm run dev` (Vite abre em localhost).
        - **Back:** abra a solution no Visual Studio e rode (perfil HTTP/Kestrel), ou `dotnet run`.

        Confirme que o front conversa com o back antes de seguir.
        """,
        [11] = """
        ## Padrões de código
        Tudo importante mora no `CLAUDE.md` de cada repo. O essencial do front:

        - Estilize com tokens **`ads-*`** (Tailwind) — **nunca** hex hardcoded nem CSS custom.
        - Todo elemento relevante recebe **`data-cy`** (`modulo-componente-elemento-tipo`).
        - Reaproveite o **Design System** antes de criar componente do zero.
        - Máximo **~400 linhas** por arquivo.
        """,
        [12] = """
        ## Fluxo git multi-repo
        O ciclo de um card, na ordem:

        1. Crie a branch a partir do **support**.
        2. Implemente e commite (`feat:` para melhoria, `fix:` para bug).
        3. **Rebase** no `support` antes de subir.
        4. Se mexeu num submódulo, faça o **bump** no `api` (aponta pro novo commit).
        5. `git push --force-with-lease` (nunca `--force` puro).
        """,
        [13] = """
        ## Jira & Bitbucket na prática
        - **Jira:** pegue o card, mova para "Em andamento", documente ao terminar.
        - **Bitbucket:** abra o **PR** com título e descrição claros e marque o **reviewer** da semana.
        - Responda os comentários do review no próprio PR.

        > Só mova o card para "pronto para teste" **depois** do merge, não na abertura do PR.
        """,
        [14] = """
        ## Pegue um card starter
        Seu primeiro card deve ser pequeno e de baixo risco — um *good-first-issue*.

        Exemplo clássico: **remover a tag de "beta"** de funcionalidades que já saíram de beta.
        O objetivo aqui é rodar o fluxo inteiro, não resolver algo difícil.
        """,
        [15] = """
        ## Crie a branch
        Padrão de nome, a partir do `support`:

        ```bash
        git checkout support
        git pull
        git checkout -b fix/MDO-XXX-support
        ```

        Troque `MDO-XXX` pelo código do seu card no Jira.
        """,
        [16] = """
        ## Implemente seguindo os padrões
        Agora o código, respeitando o `CLAUDE.md`:

        - Front e/ou back conforme o card.
        - Reaproveite componentes do Design System.
        - Adicione `data-cy` no que for interativo.
        - Commits pequenos e com mensagem clara.
        """,
        [17] = """
        ## Rode o gate
        Antes de abrir o PR, garanta que passa o mesmo gate do CI:

        ```bash
        npm run lint -- --max-warnings=0
        npm run build
        ```

        No front, **warning conta como erro** (`--max-warnings=0`). Só siga com tudo verde.
        """,
        [18] = """
        ## Abra o PR
        No Bitbucket:

        - **Título:** claro, no padrão do commit.
        - **Descrição:** o que muda e por quê.
        - **Reviewer:** o reviewer da semana.

        Não coloque nada de placeholder — o PR é real.
        """,
        [19] = """
        ## Review & ajustes
        O reviewer vai comentar. É normal e faz parte:

        - Responda cada comentário no PR.
        - Ajuste o que fizer sentido e faça push (a mesma branch atualiza o PR).
        - Se discordar, explique com educação — review é conversa.
        """,
        [20] = """
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
            // Fase A — Ambientação (conteúdo real do onboarding do RH)
            new OnboardingStep { Order = 1, FaseId = IdDaFase("Ambientação"), Title = "Bem-vindo à Agilean", Description = "Sobre a empresa, missão, visão e reconhecimento no Lean Construction.", IsCompanySpecific = true, Conteudo = Conteudos[1] },
            new OnboardingStep { Order = 2, FaseId = IdDaFase("Ambientação"), Title = "Nossa história e produtos", Description = "A linha do tempo da Agilean, os 3 produtos e as soluções por fase de maturidade.", IsCompanySpecific = true, Conteudo = Conteudos[2] },
            new OnboardingStep { Order = 3, FaseId = IdDaFase("Ambientação"), Title = "Cultura e valores", Description = "Os valores do time e os comportamentos esperados na Agilean.", IsCompanySpecific = true, Conteudo = Conteudos[3] },
            new OnboardingStep { Order = 4, FaseId = IdDaFase("Ambientação"), Title = "Liderança e squads", Description = "Quem lidera a empresa, e Mão de Obra, Quiz Quality e Agilean (desktop) — o que cada squad faz.", IsCompanySpecific = true, Conteudo = Conteudos[4] },
            new OnboardingStep { Order = 5, FaseId = IdDaFase("Ambientação"), Title = "Comunicação e rituais", Description = "Feedz, Teams, reunião de liderança, Conecta mensal e o período de experiência.", IsCompanySpecific = true, Conteudo = Conteudos[5] },
            new OnboardingStep { Order = 6, FaseId = IdDaFase("Ambientação"), Title = "Ferramentas & acessos", Description = "Instale VS Code e Visual Studio; configure o e-mail Agilean; confirme acesso ao Jira, Bitbucket e Teams.", IsCompanySpecific = true, Conteudo = Conteudos[6] },
            new OnboardingStep { Order = 7, FaseId = IdDaFase("Ambientação"), Title = "RH, DP e benefícios", Description = "Pagamento, plano de saúde, treinamentos e quem chamar no RH.", IsCompanySpecific = true, Conteudo = Conteudos[7] },

            // Fase B — Padrões
            new OnboardingStep { Order = 8, FaseId = IdDaFase("Padrões"), Title = "Padrões de código", Description = "CLAUDE.md: tokens ads-*, data-cy, sem CSS custom, máx 400 linhas, reusar o Design System.", IsCompanySpecific = true, Conteudo = Conteudos[11] },
            new OnboardingStep { Order = 9, FaseId = IdDaFase("Padrões"), Title = "Fluxo git multi-repo", Description = "Branch por card → rebase no support → bump do submódulo no api → force-with-lease.", IsCompanySpecific = true, SkillArea = SkillArea.Git, Conteudo = Conteudos[12] },
            new OnboardingStep { Order = 10, FaseId = IdDaFase("Padrões"), Title = "Jira & Bitbucket na prática", Description = "Pegar card, transições, abrir PR, review e reviewer.", IsCompanySpecific = true, Conteudo = Conteudos[13] },

            // Fase C — Ambiente técnico
            new OnboardingStep { Order = 11, FaseId = IdDaFase("Ambiente técnico"), Title = "Entenda os repositórios", Description = "agilean_portal (front), api (back), projects/contract (submódulos) e como se conectam.", IsCompanySpecific = true, Conteudo = Conteudos[8] },
            new OnboardingStep { Order = 12, FaseId = IdDaFase("Ambiente técnico"), Title = "Clone os repositórios", Description = "git clone --recurse-submodules dos repos do seu squad.", IsCompanySpecific = false, SkillArea = SkillArea.Git, Conteudo = Conteudos[9] },
            new OnboardingStep { Order = 13, FaseId = IdDaFase("Ambiente técnico"), Title = "Suba o ambiente", Description = "Instale as dependências e rode o front (Vite) e o back (dotnet).", IsCompanySpecific = false, Conteudo = Conteudos[10] },

            // Fase D — Primeiro Card
            new OnboardingStep { Order = 14, FaseId = IdDaFase("Primeiro Card"), Title = "Pegue um card starter", Description = "Um good-first-issue simples (ex.: tirar a tag de beta de funcionalidades que não são mais beta).", IsCompanySpecific = true, Conteudo = Conteudos[14] },
            new OnboardingStep { Order = 15, FaseId = IdDaFase("Primeiro Card"), Title = "Crie a branch", Description = "fix/MDO-X-support a partir do support.", IsCompanySpecific = false, SkillArea = SkillArea.Git, Conteudo = Conteudos[15] },
            new OnboardingStep { Order = 16, FaseId = IdDaFase("Primeiro Card"), Title = "Implemente seguindo os padrões", Description = "Front e/ou back, respeitando o CLAUDE.md.", IsCompanySpecific = false, Conteudo = Conteudos[16] },
            new OnboardingStep { Order = 17, FaseId = IdDaFase("Primeiro Card"), Title = "Rode o gate", Description = "npm run lint --max-warnings=0 + build.", IsCompanySpecific = true, Conteudo = Conteudos[17] },
            new OnboardingStep { Order = 18, FaseId = IdDaFase("Primeiro Card"), Title = "Abra o PR", Description = "Título, descrição e o reviewer da semana.", IsCompanySpecific = true, Conteudo = Conteudos[18] },
            new OnboardingStep { Order = 19, FaseId = IdDaFase("Primeiro Card"), Title = "Review & ajustes", Description = "Responda os comentários e ajuste o que for pedido.", IsCompanySpecific = false, Conteudo = Conteudos[19] },
            new OnboardingStep { Order = 20, FaseId = IdDaFase("Primeiro Card"), Title = "Merge + documente", Description = "Mergeie, documente no Jira e faça a transição do card. Primeiro card entregue!", IsCompanySpecific = true, Conteudo = Conteudos[20] }
        );

        await db.SaveChangesAsync();
    }
}
