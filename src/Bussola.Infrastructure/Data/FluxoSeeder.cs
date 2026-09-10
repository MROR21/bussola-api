using Bussola.Domain.Entities;
using Bussola.Domain.Nivelamento;
using Microsoft.EntityFrameworkCore;

namespace Bussola.Infrastructure.Data;

// Semeia os fluxos da "Referência viva", organizados em módulos (por squad + básico do dev).
// Idempotente + faz upgrade: preenche módulo/squad dos antigos, faz backfill do conteúdo curado
// nos fluxos que ainda estão em stub, e insere os fluxos que faltam (por título).
// Fluxos são 100% conteúdo semeado (sem dado de usuário).
public static class FluxoSeeder
{
    private const string ModuloMdO = "Mão de Obra";
    private const string ModuloQQ = "Quiz Quality";
    private const string ModuloAgilean = "Agilean (desktop)";
    private const string ModuloBasico = "Básico do dev";

    // Marca o conteúdo ainda-não-curado; usado pra detectar (e substituir) stubs no backfill.
    private const string MarcadorStub = "Conteúdo a curar";

    public static async Task SeedAsync(AppDbContext db)
    {
        // Módulos já foram semeados pela migration (SeedFaseAndModuloData) — aqui só referenciamos
        // pelo nome, nunca criamos Módulo por conta própria (isso é papel do admin agora).
        var moduloPorNome = await db.Modulos.ToDictionaryAsync(m => m.Nome, m => m.Id);
        var todos = Definicoes(moduloPorNome);

        if (!await db.Fluxos.AnyAsync())
        {
            db.Fluxos.AddRange(todos);
            await db.SaveChangesAsync();
            return;
        }

        var existentes = await db.Fluxos.Include(f => f.Modulo).ToListAsync();
        var alterou = false;

        // Fluxos do módulo Mão de Obra sem squad definido → recebem o squad MdO.
        foreach (var fluxo in existentes.Where(f => f.Modulo.Nome == ModuloMdO && f.Squad == null))
        {
            fluxo.Squad = Squad.MaoDeObra;
            alterou = true;
        }

        // Backfill de conteúdo: fluxos vazios ou ainda no stub recebem o conteúdo curado (por título).
        // Só sobrescreve stub/vazio — não encosta em conteúdo já editado à mão.
        var curadoPorTitulo = todos.ToDictionary(f => f.Titulo, f => f.Conteudo);
        foreach (var fluxo in existentes)
        {
            var precisa = string.IsNullOrWhiteSpace(fluxo.Conteudo) || fluxo.Conteudo.Contains(MarcadorStub);
            if (precisa
                && curadoPorTitulo.TryGetValue(fluxo.Titulo, out var conteudo)
                && !conteudo.Contains(MarcadorStub))
            {
                fluxo.Conteudo = conteudo;
                alterou = true;
            }
        }

        // Backfill de tag: fluxos de sistema antigos (Categoria "Sistema") recebem a tag/tópico real.
        var tagPorTitulo = todos.ToDictionary(f => f.Titulo, f => f.Categoria);
        foreach (var fluxo in existentes.Where(f => f.Categoria == "Sistema"))
        {
            if (tagPorTitulo.TryGetValue(fluxo.Titulo, out var tag) && tag != "Sistema")
            {
                fluxo.Categoria = tag;
                alterou = true;
            }
        }

        // Backfill de vídeo: fluxos sem vídeo recebem o vídeo curado (por título) — só preenche o
        // que estiver vazio, nunca sobrescreve um vídeo já editado à mão pelo Admin.
        var videoPorTitulo = todos.ToDictionary(f => f.Titulo, f => f.VideoUrl);
        foreach (var fluxo in existentes)
        {
            if (string.IsNullOrWhiteSpace(fluxo.VideoUrl)
                && videoPorTitulo.TryGetValue(fluxo.Titulo, out var video)
                && !string.IsNullOrWhiteSpace(video))
            {
                fluxo.VideoUrl = video;
                alterou = true;
            }
        }

        // Insere os fluxos que ainda não existem (por título) — ex.: o módulo Mão de Obra.
        var titulos = existentes.Select(f => f.Titulo).ToHashSet();
        var novos = todos.Where(f => !titulos.Contains(f.Titulo)).ToList();
        if (novos.Count > 0)
        {
            db.Fluxos.AddRange(novos);
            alterou = true;
        }

        if (alterou)
        {
            await db.SaveChangesAsync();
        }
    }

    // Fallback pros fluxos de sistema que ainda não têm conteúdo curado no dicionário.
    private static string StubSistema(string titulo) => $"""
        ## {titulo}
        _({MarcadorStub} — aqui entra o passo a passo da tela, com o vídeo do sistema acima.)_
        """;

    private static List<Fluxo> Definicoes(Dictionary<string, Guid> moduloPorNome)
    {
        // Cada fluxo de sistema tem uma TAG (categoria) = o tópico dentro do módulo. Isso agrupa os
        // fluxos por assunto na tela (Folha, Alocação, Orçamento...), virando um índice do módulo.
        var sistemas = new (string Modulo, Squad Squad, (string Titulo, string Descricao, string Tag)[] Fluxos)[]
        {
            // Conteúdo real, curado a partir das aulas em vídeo do wiki "Agilean na Prática"
            // (2026-09-05) — substitui os stubs mock que existiam antes (só descreviam o que ia
            // ter). "Visão geral da Mão de Obra" (resumo em texto, sem vídeo) foi REMOVIDA em
            // 2026-09-10 — só existia nesse módulo, sem equivalente em QQ/Agilean, e o Miguel
            // achou inconsistente ter um preâmbulo só numa trilha.
            (ModuloMdO, Squad.MaoDeObra, new (string, string, string)[]
            {
                ("Dashboards do Portal: longo prazo", "Dashboards do Portal com visão de longo prazo.", "Portal"),
                ("Dashboards do Portal: curto, médio prazo e financeiro", "Curto prazo, médio prazo, financeiro, medições e resultados gerais.", "Portal"),
                ("Início, ranking e relatórios do Portal", "Início, ranking, relatórios e datas de controle no Portal Admin.", "Portal"),
                ("Portal Admin: integrações e orçamento", "Integrações e orçamento no Portal Admin.", "Portal"),
                ("Mão de obra própria: histograma, funções e funcionários", "Histograma, funções, funcionários e mão de obra própria.", "Mão de obra própria e terceiros"),
                ("Mão de obra de terceiros", "Gestão da mão de obra de terceiros.", "Mão de obra própria e terceiros"),
                ("Curto prazo operacional e causas", "Curto prazo operacional e o registro de causas.", "Curto prazo operacional"),
            }),
            (ModuloQQ, Squad.QuizQuality, new (string, string, string)[]
            {
                ("Quiz Quality Portal: cadastros e preparação da obra", "Cadastros e preparação da obra no Quiz Quality Portal.", "Portal"),
                ("Quiz Quality Portal e App: qualidade admin e inspeções", "Qualidade Admin e criação/tramitação de inspeções normais e mapeadas.", "Portal"),
            }),
            (ModuloAgilean, Squad.Agilean, new (string, string, string)[]
            {
                ("Primeiros passos no Agilean", "Criar conta, empresa, projeto e os primeiros cadastros no Agilean Desktop.", "Primeiros passos"),
                ("Planejamento: linha de balanço e orçamento", "Criar linha de balanço, linha base, orçamento e reprogramação.", "Planejamento"),
                ("Reprogramação, medição e o App Agilean", "Reprogramação, medição e o uso do App Agilean.", "Planejamento"),
                ("Fechamento: reprogramação e medição", "Reprogramação e medição com fechamento, e o App Agilean.", "Fechamento"),
                ("Aprofundamento: medição de fechamento", "Vídeo extra de tira-dúvidas, aprofundando o fechamento na medição.", "Fechamento"),
            }),
        };

        var lista = new List<Fluxo>();
        // Order reinicia em 1 a cada módulo — cada squad enxerga sua própria sequência (1, 2, 3...)
        // em vez de um contador global entre módulos.
        foreach (var (modulo, squad, fluxos) in sistemas)
        {
            var ordem = 1;
            foreach (var (titulo, descricao, tag) in fluxos)
            {
                lista.Add(new Fluxo
                {
                    Order = ordem++,
                    ModuloId = moduloPorNome[modulo],
                    Squad = squad,
                    Categoria = tag,
                    Titulo = titulo,
                    Descricao = descricao,
                    Conteudo = Conteudos.GetValueOrDefault(titulo, StubSistema(titulo)),
                    VideoUrl = VideoUrls.GetValueOrDefault(titulo, string.Empty),
                });
            }
        }

        lista.AddRange(BasicoDoDev(moduloPorNome[ModuloBasico]));
        return lista;
    }

    // Vídeo (embed) por título de fluxo — o link de "Inserir/Embed" do SharePoint/Stream, não o de
    // compartilhamento comum (esse último não roda dentro de um <iframe> de outro site).
    private static readonly Dictionary<string, string> VideoUrls = new()
    {
        ["Planejamento: linha de balanço e orçamento"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=a73e6ac7-c4ea-407f-be26-0e27a827628a&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Reprogramação, medição e o App Agilean"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=1475f59c-82c8-4a5c-92a5-63c1f1c05d5c&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Fechamento: reprogramação e medição"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=2a86f41b-dc8c-450e-9e66-65c571548d3e&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Aprofundamento: medição de fechamento"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=e82ba5ad-6bd2-46b5-a900-d8887db402e4&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Dashboards do Portal: longo prazo"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=300de5ca-2daf-4c88-85c3-f614c99b2ae9&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Dashboards do Portal: curto, médio prazo e financeiro"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=55b90f80-38d6-420a-9076-9cafeda5d1f0&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Início, ranking e relatórios do Portal"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=987926bb-cb2b-43fc-abb1-20b3b0c76251&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Portal Admin: integrações e orçamento"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=23c72783-16a2-4c74-81eb-4742da6cc14b&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Mão de obra própria: histograma, funções e funcionários"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=156a9de7-14eb-4d1a-aa2c-03314be4228c&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Mão de obra de terceiros"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=5c14645e-0dab-470a-94a4-dbad4b03e9c2&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Quiz Quality Portal: cadastros e preparação da obra"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=b9bd24a1-f430-4303-9bb7-254b6773aa7c&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Quiz Quality Portal e App: qualidade admin e inspeções"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=eda30a9d-7c8a-42d1-8846-f6736ee1337c&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        // Aulas 1 e 13 (títulos originais: "Agilean na Prática 1.mp4" e "Agilean na Prática CPO
        // (13).mp4") — Miguel conseguiu o acesso em 2026-09-10, antes só o título/conteúdo existia.
        ["Primeiros passos no Agilean"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=72d249a5-a1e9-44d8-9eda-330dfb518815&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
        ["Curto prazo operacional e causas"] =
            "https://agileantech-my.sharepoint.com/personal/gabriel_ferreira_agilean_com_br/_layouts/15/embed.aspx?UniqueId=3cb26a5f-4453-4551-9f1f-436670eb2909&embed=%7B%22ust%22%3Atrue%2C%22hv%22%3A%22CopyEmbedCode%22%7D&referrer=StreamWebApp&referrerScenario=EmbedDialog.Create",
    };

    // Conteúdo (Markdown) curado dos fluxos de sistema, por título.
    // MdO: referência de verdade (o que a tela faz, conceitos-chave, pegadinhas reais dos cards).
    // QQ / Agilean: visão geral honesta — detalhe de tela a completar por alguém do squad.
    private static readonly Dictionary<string, string> Conteudos = new()
    {
        // ── Mão de Obra ─────────────────────────────────────────────────────────────
        ["Dashboards do Portal: longo prazo"] = """
        ## Dashboards do Portal: longo prazo
        - Portal Dashboards.
        - Longo prazo.
        """,
        ["Dashboards do Portal: curto, médio prazo e financeiro"] = """
        ## Dashboards do Portal: curto, médio prazo e financeiro
        - Curto prazo.
        - Médio prazo (Portal e Planner).
        - Financeiro (Dashboards e Admin).
        - Medições.
        - Resultados gerais.
        - Belgo pronto.
        """,
        ["Início, ranking e relatórios do Portal"] = """
        ## Início, ranking e relatórios do Portal
        - Início.
        - Ranking.
        - Relatórios.
        - Portal Admin: Datas de controle.
        """,
        ["Portal Admin: integrações e orçamento"] = """
        ## Portal Admin: integrações e orçamento
        - Portal Admin: Integrações.
        - Orçamento.
        """,
        ["Mão de obra própria: histograma, funções e funcionários"] = """
        ## Mão de obra própria: histograma, funções e funcionários
        - Histograma, Funções e Funcionários.
        - Mão de Obra Própria.
        """,
        ["Mão de obra de terceiros"] = """
        ## Mão de obra de terceiros
        - Mão de Obra de Terceiros.
        """,
        ["Curto prazo operacional e causas"] = """
        ## Curto prazo operacional e causas
        - Curto Prazo Operacional.
        - Causas.
        """,

        // ── Quiz Quality ─────────────────────────────────────────────────────────────
        ["Quiz Quality Portal: cadastros e preparação da obra"] = """
        ## Quiz Quality Portal: cadastros e preparação da obra
        - QuizQuality Portal.
        - Cadastros e preparação da obra.
        """,
        ["Quiz Quality Portal e App: qualidade admin e inspeções"] = """
        ## Quiz Quality Portal e App: qualidade admin e inspeções
        - Quizquality Portal e App.
        - Qualidade Admin.
        - Criação e tramitação de inspeções normais e mapeadas.
        """,

        // ── Agilean desktop ──────────────────────────────────────────────────────────
        ["Primeiros passos no Agilean"] = """
        ## Primeiros passos no Agilean
        - Criar uma conta no Agilean.
        - Criar Empresa.
        - Criar Filial.
        - Criar Projeto.
        - Criar Tipologia.
        - Criar Diagrama.
        - Salvar arquivos locais e no servidor.
        - Edição de Obras.
        - Edição de Usuários.
        - Edição de Perfis.
        """,
        ["Planejamento: linha de balanço e orçamento"] = """
        ## Planejamento: linha de balanço e orçamento
        - Criar Linha de balanço.
        - Criar Linha Base.
        - Criar Orçamento.
        - Reprogramação.
        """,
        ["Reprogramação, medição e o App Agilean"] = """
        ## Reprogramação, medição e o App Agilean
        - Reprogramação.
        - Medição.
        - App Agilean.
        """,
        ["Fechamento: reprogramação e medição"] = """
        ## Fechamento: reprogramação e medição
        - Reprogramação com Fechamento.
        - Medição com fechamento.
        - App Agilean.
        """,
        ["Aprofundamento: medição de fechamento"] = """
        ## Aprofundamento: medição de fechamento
        Vídeo extra de tira-dúvidas, aprofundando o tema de fechamento na medição.
        """,
    };

    // Fluxos genéricos do dia a dia do dev (os que já existiam no #4), agora no módulo "Básico do dev".
    private static IEnumerable<Fluxo> BasicoDoDev(Guid moduloBasicoId)
    {
        var basicos = new List<Fluxo>
        {
            new()
            {
                Categoria = "Arquitetura",
                Titulo = "Arquitetura do sistema",
                Descricao = "Como o sistema é organizado: multi-repo, back e front.",
                Conteudo = """
                ## Arquitetura do sistema
                Uma visão geral de como o sistema da Agilean é organizado — pra você saber *onde*
                mexer antes de *como* mexer.

                ## Multi-repositório
                - **agilean_portal** — o front (React + TypeScript + Vite).
                - **api** — o back (C# / .NET).
                - **projects** e **contract** — entram como **submódulos** do `api` (que aponta pra
                  um commit específico de cada; daí o "bump de submódulo").

                ## Back (C# / .NET)
                Organizado em **camadas** e no estilo **CQRS** (comandos escrevem, queries leem):
                - **Query/Command** → **Handler** (a regra) → **Repository** (dados via **Dapper**).
                - O banco devolve uma **View** (projeção SQL), que o **AutoMapper** converte na
                  **Response** (o DTO que sai pra API). Fluxo mental: *View (banco) → AutoMapper →
                  Response (API)*.
                - Regra de negócio mora no domínio/handler, **não** no controller.

                ## Front (React + TS)
                - **Design System** próprio em `src/agilean-design-system` — reusar antes de criar.
                - **Estilo:** Tailwind com tokens **`ads-*`** (tema troca sozinho); sem CSS custom.
                - **Estado:** **Zustand** (sessão/obra) + **TanStack React Query** (dados do servidor).
                - **Tabelas:** **AgGrid Enterprise v31** (locale PT) pras grids pesadas.
                - **Padrão de tela:** `context/` (estado) + `services/` (chamadas tipadas que
                  retornam `Result<T>` via `api()`) + hooks (`useColumns`, `useNomeDoHook`).

                ## Como se conectam
                O front chama a API por HTTP; a API lê/escreve via Dapper e devolve Responses. Front
                e back são **desacoplados** — dá pra evoluir a tela com mock e, na integração, trocar
                só o corpo do service.

                > Onde mexer: tela/estilo → `agilean_portal`. Regra/dado → `api` (e o submódulo
                > certo). Na dúvida, o `CLAUDE.md` de cada repo manda.
                """,
            },
            new()
            {
                Categoria = "Git & PR",
                Titulo = "Abrir um PR",
                Descricao = "Do commit ao pull request no Bitbucket.",
                Conteudo = """
                ## Abrir um PR
                1. Garanta a branch atualizada (rebase no `support`).
                2. `git push --force-with-lease`.
                3. No Bitbucket, abra o PR: **título** no padrão do commit, **descrição** do que muda.
                4. Marque o **reviewer da semana**.

                > Nada de placeholder — o PR é real e vai pra review.
                """,
            },
            new()
            {
                Categoria = "Git & PR",
                Titulo = "Rebase no support",
                Descricao = "Trazer sua branch pro topo do support antes de subir.",
                Conteudo = """
                ## Rebase no support
                ```bash
                git fetch origin
                git rebase origin/support
                ```
                Resolva conflitos, `git add` nos arquivos e `git rebase --continue`.
                Ao final, suba com `git push --force-with-lease` (nunca `--force` puro).
                """,
            },
            new()
            {
                Categoria = "Git & PR",
                Titulo = "Bump de submódulo",
                Descricao = "Apontar o api pro novo commit do submódulo.",
                Conteudo = """
                ## Bump de submódulo
                Quando você commita em `projects`/`contract`, o `api` precisa apontar pro novo commit:
                ```bash
                cd api
                git add projects            # ou contract
                git commit -m "chore: bump submodule"
                ```
                Sem o bump, o CI compila a versão antiga do submódulo.
                """,
            },
            new()
            {
                Categoria = "Jira",
                Titulo = "Pegar e mover um card",
                Descricao = "Assumir um card e sinalizar que está trabalhando nele.",
                Conteudo = """
                ## Pegar e mover um card
                1. No board do seu squad, escolha um card.
                2. Atribua a você.
                3. Mova para **"Em andamento"**.

                Anote o código (ex.: `MDO-123`) — ele vira o nome da sua branch.
                """,
            },
            new()
            {
                Categoria = "Jira",
                Titulo = "Documentar e transicionar",
                Descricao = "Fechar o card certo depois do merge.",
                Conteudo = """
                ## Documentar e transicionar
                Só **depois do merge**:
                1. Comente no card o que foi feito (e o link do PR).
                2. Faça a transição para o próximo status (ex.: "pronto para teste").

                Não mova o card na abertura do PR — só quando ele estiver mergeado.
                """,
            },
            new()
            {
                Categoria = "Padrões",
                Titulo = "Estilo com tokens ads-*",
                Descricao = "Como estilizar sem CSS custom.",
                Conteudo = """
                ## Estilo com tokens ads-*
                Use utilitários Tailwind com os tokens do Design System — o tema (claro/escuro) troca sozinho:
                - Texto: `text-ads-on-surface` · muted: `text-ads-on-surface-variant`
                - Ação: `bg-ads-primary` · erro: `text-ads-error`

                **Nunca** hex hardcoded nem `.css` próprio. O lint reprova classe custom.
                """,
            },
            new()
            {
                Categoria = "Padrões",
                Titulo = "data-cy nos elementos",
                Descricao = "Marcar elementos para os testes.",
                Conteudo = """
                ## data-cy nos elementos
                Todo elemento interativo/relevante recebe um `data-cy` no formato
                `modulo-componente-elemento-tipo` (kebab-case):
                ```tsx
                <button data-cy="folha-detalhe-salvar-btn">Salvar</button>
                ```
                O prefixo de módulo é único por tela.
                """,
            },
            new()
            {
                Categoria = "Padrões",
                Titulo = "Rodar o gate",
                Descricao = "Lint + build, o mesmo do CI.",
                Conteudo = """
                ## Rodar o gate
                Antes de abrir o PR:
                ```bash
                npm run lint -- --max-warnings=0
                npm run build
                ```
                No front, **warning conta como erro**. Só suba com tudo verde.
                """,
            },
            new()
            {
                Categoria = "Ambiente",
                Titulo = "Clonar com submódulos",
                Descricao = "Trazer os repos com os submódulos juntos.",
                Conteudo = """
                ## Clonar com submódulos
                ```bash
                git clone --recurse-submodules <url>
                ```
                Se já clonou sem eles:
                ```bash
                git submodule update --init --recursive
                ```
                """,
            },
            new()
            {
                Categoria = "Ambiente",
                Titulo = "Subir front e back",
                Descricao = "Rodar o ambiente local.",
                Conteudo = """
                ## Subir front e back
                - **Front:** `npm install` e `npm run dev` (Vite).
                - **Back:** abra a solution no Visual Studio (perfil HTTP/Kestrel) ou `dotnet run`.

                Confirme que o front conversa com o back antes de codar.
                """,
            },
        };

        var ordem = 1;
        foreach (var fluxo in basicos)
        {
            fluxo.Order = ordem++;
            fluxo.ModuloId = moduloBasicoId;
            fluxo.VideoUrl = string.Empty;
        }
        return basicos;
    }
}
