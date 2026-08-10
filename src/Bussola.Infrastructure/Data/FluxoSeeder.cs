using Bussola.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bussola.Infrastructure.Data;

// Semeia os fluxos da "Referência viva", organizados em módulos (por squad + básico do dev).
// Idempotente + faz upgrade: se a tabela já tem os antigos (sem módulo), preenche o módulo e
// insere os fluxos que faltam (por título). Fluxos são 100% conteúdo semeado (sem dado de usuário).
public static class FluxoSeeder
{
    private const string ModuloMdO = "Mão de Obra";
    private const string ModuloBasico = "Básico do dev";

    public static async Task SeedAsync(AppDbContext db)
    {
        var todos = Definicoes();

        if (!await db.Fluxos.AnyAsync())
        {
            db.Fluxos.AddRange(todos);
            await db.SaveChangesAsync();
            return;
        }

        var existentes = await db.Fluxos.ToListAsync();
        var alterou = false;

        // Antigos sem módulo (seed do #4) → viram "Básico do dev".
        foreach (var fluxo in existentes.Where(f => string.IsNullOrWhiteSpace(f.Modulo)))
        {
            fluxo.Modulo = ModuloBasico;
            alterou = true;
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

    // Stub de conteúdo pros fluxos do sistema que ainda serão curados (com o vídeo real).
    private static string StubSistema(string titulo) => $"""
        ## {titulo}
        _(Conteúdo a curar — aqui entra o passo a passo da tela, com o vídeo do sistema acima.)_
        """;

    private static List<Fluxo> Definicoes()
    {
        var mdo = new (string Titulo, string Descricao)[]
        {
            ("Visão geral da Mão de Obra", "O que a MdO controla: custos e alocação de equipe na obra."),
            ("Folha de pagamento — visão geral", "Como a folha organiza os pagamentos do período."),
            ("Detalhe e ajuste da folha", "Ajustar valores e sugeridos dentro de uma folha."),
            ("Alocação de equipe", "Distribuir funcionários e pesos numa alocação."),
            ("Adicionar e incluir alocações", "Incluir novas alocações e novos funcionários."),
            ("Orçamento de mão de obra", "Como o orçamento de MdO é montado."),
            ("Despesas indiretas", "O que são e como entram no orçamento."),
            ("Pacotes de trabalho", "Agrupar serviços em pacotes."),
            ("Resolver pendências", "Tratar itens pendentes de uma folha/alocação."),
            ("Distribuição automática", "Distribuir valores automaticamente pela equipe."),
            ("Devolver valor a pagar", "Quando e como devolver um valor a pagar."),
            ("Custos e relatórios", "Ler os custos consolidados da obra."),
            ("Ocultar e exibir funcionário", "Controlar a visibilidade de um funcionário."),
        };

        var lista = new List<Fluxo>();
        var ordem = 1;
        foreach (var (titulo, descricao) in mdo)
        {
            lista.Add(new Fluxo
            {
                Order = ordem++,
                Modulo = ModuloMdO,
                Categoria = "Sistema",
                Titulo = titulo,
                Descricao = descricao,
                Conteudo = StubSistema(titulo),
                VideoUrl = string.Empty,
            });
        }

        lista.AddRange(BasicoDoDev(ref ordem));
        return lista;
    }

    // Fluxos genéricos do dia a dia do dev (os que já existiam no #4), agora no módulo "Básico do dev".
    private static IEnumerable<Fluxo> BasicoDoDev(ref int ordem)
    {
        var basicos = new List<Fluxo>
        {
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

        foreach (var fluxo in basicos)
        {
            fluxo.Order = ordem++;
            fluxo.Modulo = ModuloBasico;
            fluxo.VideoUrl = string.Empty;
        }
        return basicos;
    }
}
