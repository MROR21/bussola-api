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

        // Fluxos do módulo Mão de Obra sem squad definido → recebem o squad MdO.
        foreach (var fluxo in existentes.Where(f => f.Modulo == ModuloMdO && f.Squad == null))
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

    private static List<Fluxo> Definicoes()
    {
        var sistemas = new (string Modulo, Squad Squad, (string Titulo, string Descricao)[] Fluxos)[]
        {
            (ModuloMdO, Squad.MaoDeObra, new (string, string)[]
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
            }),
            (ModuloQQ, Squad.QuizQuality, new (string, string)[]
            {
                ("Visão geral do Quiz Quality", "O que o squad de qualidade/inspeção faz."),
                ("Inspeções", "Como criar e conduzir uma inspeção."),
                ("Relatórios de qualidade", "Ler e exportar os relatórios de qualidade."),
            }),
            (ModuloAgilean, Squad.Agilean, new (string, string)[]
            {
                ("Visão geral do Agilean", "O aplicativo de planejamento da obra."),
                ("Planejamento", "Montar o planejamento no desktop."),
                ("Acompanhamento", "Acompanhar o avanço do plano."),
            }),
        };

        var lista = new List<Fluxo>();
        var ordem = 1;
        foreach (var (modulo, squad, fluxos) in sistemas)
        {
            foreach (var (titulo, descricao) in fluxos)
            {
                lista.Add(new Fluxo
                {
                    Order = ordem++,
                    Modulo = modulo,
                    Squad = squad,
                    Categoria = "Sistema",
                    Titulo = titulo,
                    Descricao = descricao,
                    Conteudo = Conteudos.GetValueOrDefault(titulo, StubSistema(titulo)),
                    VideoUrl = string.Empty,
                });
            }
        }

        lista.AddRange(BasicoDoDev(ref ordem));
        return lista;
    }

    // Conteúdo (Markdown) curado dos fluxos de sistema, por título.
    // MdO: referência de verdade (o que a tela faz, conceitos-chave, pegadinhas reais dos cards).
    // QQ / Agilean: visão geral honesta — detalhe de tela a completar por alguém do squad.
    private static readonly Dictionary<string, string> Conteudos = new()
    {
        // ── Mão de Obra ─────────────────────────────────────────────────────────────
        ["Visão geral da Mão de Obra"] = """
        ## Visão geral da Mão de Obra
        O módulo de **Mão de Obra (MdO)** controla o **custo de pessoas** numa obra: quanto cada
        funcionário recebe, como esse valor se distribui entre as frentes de serviço, e como isso
        se compara ao que foi orçado.

        ## As três pontas
        - **Orçamento** — o quanto está previsto gastar com mão de obra (por serviço/pacote).
        - **Alocação** — como a equipe real é distribuída nas frentes (com pesos).
        - **Folha** — o pagamento efetivo do período, que consome o orçado.

        > **Ideia central:** cada real pago a um funcionário precisa "cair" em algum lugar do
        > orçamento. A MdO é o que amarra *pessoa → serviço → custo*.

        Comece pela **Folha** (o dia a dia) e depois entenda **Alocação** e **Orçamento**, que
        alimentam os valores sugeridos.
        """,
        ["Folha de pagamento — visão geral"] = """
        ## Folha de pagamento — visão geral
        A **folha** agrupa os pagamentos de mão de obra de um **período** (competência). Lista os
        funcionários, o quanto cada um tem **a pagar** e o quanto já foi **pago/retirado**.

        ## Status da folha (importa muito)
        - **Aberta** — pode editar valores, ajustar alocações, resolver pendências.
        - **Em aprovação** / **Aprovada** — **bloqueada** para edição. A regra é: só é editável
          enquanto está *aberta* (`IsOpen`).

        ## Conceitos que voltam sempre
        - **A pagar** = o que ainda falta pagar no período.
        - **Retirada / pagamento** = o que já saiu; o **saldo** nasce da diferença.
        - **Pendências** = itens que precisam de ação antes de fechar (ver o fluxo de pendências).

        A folha é a porta de entrada do módulo — ajuste, sugerido, pendências e devolução acontecem
        todos a partir dela.
        """,
        ["Detalhe e ajuste da folha"] = """
        ## Detalhe e ajuste da folha
        Ao abrir uma folha, o **detalhe** mostra cada funcionário e seus valores. O **ajuste** é
        onde você corrige quanto cada um recebe.

        ## Sugerido vs. manual
        - O sistema calcula um **valor sugerido** por funcionário (a partir da alocação e do saldo).
        - Você pode **aplicar o sugerido** (individual ou em massa) ou digitar um **valor manual**.
        - Aplicar sugerido só faz sentido quando o **sugerido é > 0**.

        ## Pegadinhas reais
        - O ajuste só é possível com a folha **aberta**; em aprovação/aprovada fica só leitura.
        - Valores digitados **não podem vazar entre funcionários/alocações** — cada linha é
          independente (já foi uma classe de bug aqui).
        - Ao ocultar quem tem saldo zero, o cálculo deve **netar pagamento e retirada**, não olhar
          só o pagamento.

        O ajuste é o coração da folha — é aqui que o valor final de cada pessoa é definido.
        """,
        ["Alocação de equipe"] = """
        ## Alocação de equipe
        A **alocação** distribui os funcionários de uma equipe entre as frentes de serviço, usando
        **pesos**. O peso define que fatia do custo cai em cada funcionário/serviço.

        ## Dois modos de peso
        - **Por salário** — o peso sai do salário de cada um (proporcional).
        - **Manual** — você define o peso na mão.

        > **Regra importante:** se um funcionário **não tem salário** cadastrado, o modo *por
        > salário* não fecha — a alocação **cai automaticamente para peso manual** e o salvamento
        > fica bloqueado até os pesos serem válidos.

        ## Fluxo típico
        1. Escolha a equipe.
        2. Defina o modo de peso (salário ou manual).
        3. Ajuste os pesos até bater o total.
        4. Salve — a alocação vira base do **sugerido** na folha.

        Alocação bem-feita = sugerido correto na folha. As duas coisas andam juntas.
        """,
        ["Adicionar e incluir alocações"] = """
        ## Adicionar e incluir alocações
        Além de editar uma alocação existente, você pode **incluir novas alocações** e **adicionar
        funcionários** a uma equipe que já existe.

        ## Duas ações parecidas, mas diferentes
        - **Adicionar funcionário** a uma alocação — entra mais uma pessoa; os pesos se rebalanceiam.
        - **Incluir alocação** — cria uma nova distribuição (ex.: outra frente/serviço).

        ## Pegadinhas
        - Ao adicionar um funcionário **sem salário**, o modo cai para **manual** (mesmo gating da
          alocação normal).
        - O **sugerido** aqui é calculado **por funcionário**, não no agregado — senão o excesso de
          um sobre-pago "come" o avanço de outro.
        - Dá pra adicionar funcionário a uma alocação de criação que **já tem equipe**.

        Use quando a equipe real mudou: alguém entrou, ou surgiu uma frente nova no período.
        """,
        ["Orçamento de mão de obra"] = """
        ## Orçamento de mão de obra
        O **orçamento de MdO** é o quanto está **previsto** gastar com pessoas na obra, organizado
        por serviço/pacote. É o alvo contra o qual a folha (o gasto real) é comparada.

        ## Como se monta
        - Cada **serviço** tem um custo de mão de obra previsto.
        - Serviços podem ser agrupados em **pacotes de trabalho**.
        - Há também as **despesas indiretas** (o que não é mão de obra direta de um serviço).

        ## Orçamento manual vs. calculado
        - Parte pode vir calculada; parte pode ser **manual** (você digita o previsto).
        - Em orçamento manual, as **despesas indiretas** também entram — e precisam aparecer no
          consolidado, sem "sumir".

        O orçamento é a régua: sem ele, "gastou muito ou pouco?" não tem resposta. A folha preenche
        o realizado; o orçamento diz o esperado.
        """,
        ["Despesas indiretas"] = """
        ## Despesas indiretas
        **Despesas indiretas** são custos de mão de obra que **não pertencem diretamente a um
        serviço** — apoio, encargos, estrutura. Entram no orçamento por fora dos serviços diretos,
        mas contam no **custo total**.

        ## Onde aparecem
        - No **orçamento** (inclusive no manual), como um item próprio.
        - No **consolidado de custos**, somando ao total da obra.

        ## Pegadinhas
        - Em orçamento manual, a despesa indireta precisa **entrar de fato** no cálculo — já houve
          bug de ela ficar de fora.
        - No item sintético (o agrupador) não pode aparecer **traço solto / valor órfão** — o
          agregado tem que fechar com os filhos.

        Pense nelas como o "custo de estar na obra" que não cabe em nenhum serviço específico, mas
        que alguém paga.
        """,
        ["Pacotes de trabalho"] = """
        ## Pacotes de trabalho
        Um **pacote de trabalho** agrupa vários serviços numa unidade só — pra orçar, alocar e
        acompanhar o custo de forma consolidada, em vez de serviço a serviço.

        ## Pra que serve
        - **Organizar** frentes que andam juntas (ex.: tudo de uma etapa da obra).
        - **Consolidar** custo e avanço no nível do pacote.
        - Servir de base pra ações em lote (as mesmas da folha).

        ## Pegadinhas
        - Ações que existem na folha (como **devolver valor a pagar**) também aparecem em pacotes e
          seguem as **mesmas regras de elegibilidade** — o botão desabilita quando não se aplica,
          com o motivo nas exceções.
        - Um pacote com itens crus precisa de **guarda** pra não contar valor de funcionário oculto
          no dashboard.

        Pacote = a "pasta" que junta serviços afins pra você raciocinar por etapa, não por item solto.
        """,
        ["Resolver pendências"] = """
        ## Resolver pendências
        **Pendências** são itens de uma folha/alocação que precisam de ação antes de fechar o
        período — funcionário com valor a definir, retirada sem contrapartida, ou distribuição que
        não fechou.

        ## O que costuma pendenciar
        - Funcionário com **retirada anterior** mas **sem "a pagar"** definido.
        - Distribuição/peso que não somou o total.
        - Valores que precisam de aplicação do sugerido ou de ajuste manual.

        ## No modal de pendências
        - Cada linha traz o funcionário e o que falta.
        - Você resolve aplicando sugerido, ajustando valor, ou tratando a exceção.

        > **Do lado do usuário:** se resolver "todas de uma vez" travar a tela, é bug (já houve um
        > loop de re-render), não uso errado.

        Zerar as pendências é o pré-requisito pra fechar/aprovar a folha com segurança.
        """,
        ["Distribuição automática"] = """
        ## Distribuição automática
        A **distribuição automática** espalha um valor pela equipe de uma vez, em vez de você digitar
        funcionário por funcionário. É atalho pra alocar/pagar rápido respeitando os pesos.

        ## Como funciona
        - Você define o total (ou usa o sugerido) e o sistema **reparte** pela equipe.
        - A repartição respeita os **pesos** da alocação (salário ou manual).
        - Há opção de **mínimo permitido por %** — um piso pra ninguém ficar abaixo de certa fatia.

        ## Pegadinhas
        - O **mínimo por %** não pode ser reaplicado cegamente "em todo mundo" — quem tem bloqueio
          não deve ser empurrado pro mesmo % de quem está livre.
        - O sugerido que alimenta a distribuição precisa ser o **correto por funcionário**.

        Use quando quer velocidade e a regra de peso já está certa — a automática só é tão boa quanto
        a alocação por trás dela.
        """,
        ["Devolver valor a pagar"] = """
        ## Devolver valor a pagar
        **Devolver valor a pagar** retorna um valor que estava marcado a pagar para um funcionário —
        por correção, ou porque o pagamento não vai acontecer naquele período.

        ## Elegibilidade (o ponto central)
        - O botão só deve agir quando o funcionário/valor **é elegível** à devolução.
        - Quando **não** é elegível, o botão fica **desabilitado**, e um **modal de exceções**
          explica *por que* aqueles itens não podem ser devolvidos.
        - A mesma regra vale nas telas de **pacotes**, não só na folha.

        ## Fluxo
        1. Selecione o(s) funcionário(s).
        2. Se elegível, confirme a devolução; senão, o modal lista as exceções.
        3. O valor volta pra "a pagar" / sai do pago, conforme o caso.

        > **Regra de ouro:** checar elegibilidade **antes** de habilitar. Botão que age sem checar é
        > fonte de erro.
        """,
        ["Custos e relatórios"] = """
        ## Custos e relatórios
        A visão de **custos** consolida quanto a obra gastou com mão de obra e compara com o orçado.
        É onde o gestor lê o "placar" do período.

        ## O que você lê aqui
        - **Realizado** (o que a folha efetivou) vs. **orçado** (a previsão).
        - Custo por **serviço**, por **pacote**, e com as **despesas indiretas** somadas.
        - Consolidado da obra inteira.

        ## Pegadinhas
        - Um **funcionário oculto** (saldo zerado) **não pode** contaminar o total — nem via
          alocação, nem via pacote com itens crus.
        - Tabelas grandes precisam de **virtualização** — sem isso a tela trava.

        É a foto final: se orçamento é o alvo e a folha é o tiro, os relatórios mostram o quão perto
        você acertou.
        """,
        ["Ocultar e exibir funcionário"] = """
        ## Ocultar e exibir funcionário
        **Ocultar** um funcionário tira ele da visão da folha/alocação quando ele não tem mais nada a
        tratar no período (tipicamente **saldo zero**). **Exibir** desfaz isso.

        ## A regra do saldo (importante)
        - Só deve poder ocultar quem está **de fato quitado** — e "quitado" significa **netar
          pagamento e retirada**, não olhar só o pagamento. Quem recebeu retirada ainda pode ter saldo.

        ## O que ocultar NÃO pode fazer
        - Não pode **sumir** com valor no consolidado: alocação e pacote do oculto saem das contas
          visíveis, mas sem quebrar o total.
        - Um funcionário ocultado **não deve reaparecer** no ajuste/detalhe da folha (a visão ao vivo
          e o *snapshot* têm que concordar — a correção certa mora no snapshot).

        Ocultar é organização visual com regra de negócio embutida: esconde o que está resolvido, sem
        falsear número nenhum.
        """,

        // ── Quiz Quality (visão geral — completar pelo squad) ───────────────────────
        ["Visão geral do Quiz Quality"] = """
        ## Visão geral do Quiz Quality
        O **Quiz Quality (QQ)** é o squad de **inspeção e qualidade** — a parte do produto que checa
        se o que foi executado na obra está conforme.

        > _Visão geral inicial — os detalhes de tela devem ser completados por alguém do squad QQ._

        ## Ideia geral
        - Cria e conduz **inspeções** de qualidade.
        - Gera **relatórios** do que foi inspecionado.
        - Alimenta a decisão de aceitar / rejeitar / retrabalhar uma frente.

        Se você entrou no QQ, use este módulo como esqueleto e complete cada fluxo com o passo a passo
        real da sua tela.
        """,
        ["Inspeções"] = """
        ## Inspeções
        A **inspeção** é o registro de uma verificação de qualidade em campo: o que foi checado, o
        resultado e as evidências.

        > _Conteúdo inicial — completar com o passo a passo real por alguém do squad QQ._

        ## Em linhas gerais
        - Criar uma inspeção (o que / onde inspecionar).
        - Registrar itens e resultado (conforme / não conforme).
        - Anexar evidências e concluir.

        O detalhe de cada campo e botão fica pendente de curadoria do squad.
        """,
        ["Relatórios de qualidade"] = """
        ## Relatórios de qualidade
        Os **relatórios** consolidam as inspeções: o que passou, o que reprovou e onde estão os pontos
        de atenção da obra.

        > _Conteúdo inicial — completar com o passo a passo real por alguém do squad QQ._

        ## Em linhas gerais
        - Ler o resultado consolidado das inspeções.
        - Filtrar por período / frente.
        - Exportar quando preciso.

        Complete com as opções reais de filtro e exportação da tela.
        """,

        // ── Agilean desktop (visão geral — completar pelo squad) ────────────────────
        ["Visão geral do Agilean"] = """
        ## Visão geral do Agilean
        O **Agilean (desktop)** é o aplicativo de **planejamento da obra** — onde o plano é montado e
        acompanhado, fora do portal web.

        > _Visão geral inicial — completar com detalhes de tela por alguém do squad Agilean._

        ## Ideia geral
        - Montar o **planejamento** (o que fazer, quando).
        - Acompanhar o **avanço** contra o previsto.

        Use como esqueleto; o passo a passo real do desktop deve ser preenchido por quem é do squad.
        """,
        ["Planejamento"] = """
        ## Planejamento
        No **planejamento** você monta o plano da obra no desktop: as etapas, a sequência e os prazos.

        > _Conteúdo inicial — completar com o passo a passo real por alguém do squad Agilean._

        ## Em linhas gerais
        - Definir etapas / frentes e sua ordem.
        - Estabelecer prazos e dependências.

        O detalhe de cada tela do desktop fica pendente de curadoria do squad.
        """,
        ["Acompanhamento"] = """
        ## Acompanhamento
        O **acompanhamento** compara o avanço real da obra com o que foi planejado, pra enxergar
        atraso / adiantamento cedo.

        > _Conteúdo inicial — completar com o passo a passo real por alguém do squad Agilean._

        ## Em linhas gerais
        - Registrar avanço das etapas.
        - Comparar previsto vs. realizado.

        Complete com as telas e indicadores reais do desktop.
        """,
    };

    // Fluxos genéricos do dia a dia do dev (os que já existiam no #4), agora no módulo "Básico do dev".
    private static IEnumerable<Fluxo> BasicoDoDev(ref int ordem)
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

        foreach (var fluxo in basicos)
        {
            fluxo.Order = ordem++;
            fluxo.Modulo = ModuloBasico;
            fluxo.VideoUrl = string.Empty;
        }
        return basicos;
    }
}
