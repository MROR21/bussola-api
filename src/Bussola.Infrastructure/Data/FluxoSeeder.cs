using Bussola.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bussola.Infrastructure.Data;

// Semeia os fluxos da "Referência viva". Idempotente: só insere se a tabela estiver vazia.
public static class FluxoSeeder
{
    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.Fluxos.AnyAsync())
        {
            return;
        }

        db.Fluxos.AddRange(
            new Fluxo
            {
                Order = 1,
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
            new Fluxo
            {
                Order = 2,
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
            new Fluxo
            {
                Order = 3,
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
            new Fluxo
            {
                Order = 4,
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
            new Fluxo
            {
                Order = 5,
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
            new Fluxo
            {
                Order = 6,
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
            new Fluxo
            {
                Order = 7,
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
            new Fluxo
            {
                Order = 8,
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
            new Fluxo
            {
                Order = 9,
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
            new Fluxo
            {
                Order = 10,
                Categoria = "Ambiente",
                Titulo = "Subir front e back",
                Descricao = "Rodar o ambiente local.",
                Conteudo = """
                ## Subir front e back
                - **Front:** `npm install` e `npm run dev` (Vite).
                - **Back:** abra a solution no Visual Studio (perfil HTTP/Kestrel) ou `dotnet run`.

                Confirme que o front conversa com o back antes de codar.
                """,
            }
        );

        await db.SaveChangesAsync();
    }
}
