using Bussola.Domain.Entities;
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
        // Módulos e Squads já foram semeados pelas migrations (SeedFaseAndModuloData/
        // SeedSquadData) — aqui só referenciamos pelo nome, nunca criamos Módulo/Squad por conta
        // própria (isso é papel do admin agora).
        var moduloPorNome = await db.Modulos.ToDictionaryAsync(m => m.Nome, m => m.Id);
        var squadPorNome = await db.Squads.ToDictionaryAsync(s => s.Nome, s => s.Id);
        var todos = Definicoes(moduloPorNome, squadPorNome);

        if (!await db.Fluxos.AnyAsync())
        {
            db.Fluxos.AddRange(todos);
            await db.SaveChangesAsync();
            return;
        }

        var existentes = await db.Fluxos.Include(f => f.Modulo).ToListAsync();
        var alterou = false;

        // Fluxos do módulo Mão de Obra sem squad definido → recebem o squad MdO.
        foreach (var fluxo in existentes.Where(f => f.Modulo.Nome == ModuloMdO && f.SquadId == null))
        {
            fluxo.SquadId = squadPorNome[ModuloMdO];
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

        // Backfill de tipo: fluxos que nasceram como "Fluxo" (o default, antes de Tipo existir)
        // mas que a definição curada marca como Documentação (ex.: Básico do dev, tudo texto, sem
        // vídeo) viram Documentação. Só nessa direção — nunca desfaz uma escolha manual que já
        // tenha virado Documentação → Fluxo (ex.: admin add um vídeo a um desses depois).
        var tipoPorTitulo = todos.ToDictionary(f => f.Titulo, f => f.Tipo);
        foreach (var fluxo in existentes)
        {
            if (fluxo.Tipo == TipoConteudo.Fluxo
                && tipoPorTitulo.TryGetValue(fluxo.Titulo, out var tipo)
                && tipo == TipoConteudo.Documentacao)
            {
                fluxo.Tipo = TipoConteudo.Documentacao;
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

    private static List<Fluxo> Definicoes(Dictionary<string, Guid> moduloPorNome, Dictionary<string, Guid> squadPorNome)
    {
        // Cada fluxo de sistema tem uma TAG (categoria) = o tópico dentro do módulo. Isso agrupa os
        // fluxos por assunto na tela (Folha, Alocação, Orçamento...), virando um índice do módulo.
        // SquadNome é o mesmo texto de ModuloMdO/ModuloQQ/ModuloAgilean de propósito — os 3 módulos
        // de squad têm nome idêntico ao squad correspondente (ver SeedSquadData).
        var sistemas = new (string Modulo, string SquadNome, (string Titulo, string Descricao, string Tag)[] Fluxos)[]
        {
            // Conteúdo real, curado a partir das aulas em vídeo do wiki "Agilean na Prática"
            // (2026-09-05) — substitui os stubs mock que existiam antes (só descreviam o que ia
            // ter). Cada módulo abre com uma "Visão geral" (resumo em texto, sem vídeo) — mesma
            // base/estrutura nos 3 (intro + "as três pontas" + ideia central + por onde começar),
            // pedido do Miguel 2026-09-10 pra ficar consistente entre os squads.
            (ModuloMdO, ModuloMdO, new (string, string, string)[]
            {
                ("Visão geral da Mão de Obra", "O que a MdO controla: custos e alocação de equipe na obra.", "Visão geral"),
                ("Dashboards do Portal: longo prazo", "Dashboards do Portal com visão de longo prazo.", "Portal"),
                ("Dashboards do Portal: curto, médio prazo e financeiro", "Curto prazo, médio prazo, financeiro, medições e resultados gerais.", "Portal"),
                ("Início, ranking e relatórios do Portal", "Início, ranking, relatórios e datas de controle no Portal Admin.", "Portal"),
                ("Portal Admin: integrações e orçamento", "Integrações e orçamento no Portal Admin.", "Portal"),
                ("Mão de obra própria: histograma, funções e funcionários", "Histograma, funções, funcionários e mão de obra própria.", "Mão de obra própria e terceiros"),
                ("Mão de obra de terceiros", "Gestão da mão de obra de terceiros.", "Mão de obra própria e terceiros"),
                ("Curto prazo operacional e causas", "Curto prazo operacional e o registro de causas.", "Curto prazo operacional"),
            }),
            (ModuloQQ, ModuloQQ, new (string, string, string)[]
            {
                ("Visão geral do Quiz Quality", "O que o QQ controla: inspeções de qualidade e não conformidades na obra.", "Visão geral"),
                ("Quiz Quality Portal: cadastros e preparação da obra", "Cadastros e preparação da obra no Quiz Quality Portal.", "Portal"),
                ("Quiz Quality Portal e App: qualidade admin e inspeções", "Qualidade Admin e criação/tramitação de inspeções normais e mapeadas.", "Portal"),
            }),
            (ModuloAgilean, ModuloAgilean, new (string, string, string)[]
            {
                ("Visão geral do Agilean (desktop)", "O que o Agilean controla: planejamento e acompanhamento da obra.", "Visão geral"),
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
        foreach (var (modulo, squadNome, fluxos) in sistemas)
        {
            var ordem = 1;
            foreach (var (titulo, descricao, tag) in fluxos)
            {
                lista.Add(new Fluxo
                {
                    Order = ordem++,
                    ModuloId = moduloPorNome[modulo],
                    SquadId = squadPorNome[squadNome],
                    Categoria = tag,
                    Titulo = titulo,
                    Descricao = descricao,
                    Conteudo = Conteudos.GetValueOrDefault(titulo, StubSistema(titulo)),
                    VideoUrl = VideoUrls.GetValueOrDefault(titulo, string.Empty),
                });
            }
        }

        lista.AddRange(BasicoDoDev(moduloPorNome[ModuloBasico]));
        lista.AddRange(DocumentacaoSquads(moduloPorNome, squadPorNome));
        return lista;
    }

    // Documentação real trazida da wiki interna (TI - Fábrica de Software > squad > {planner,
    // workforce}, 2026-09-19) — só os squads/páginas que já tinham conteúdo preenchido lá
    // (Quiz Quality ainda não tem nada na wiki). Tipo=Documentacao: é referência escrita, não
    // "aula" de sistema, então cai na aba Documentação/Guia do módulo do squad, não em Fluxo.
    // Continua a numeração de Order de cada módulo (Mão de Obra vai até 8, Agilean até 6 acima).
    private static IEnumerable<Fluxo> DocumentacaoSquads(Dictionary<string, Guid> moduloPorNome, Dictionary<string, Guid> squadPorNome)
    {
        var docs = new List<(string Modulo, int Order, string Categoria, string Titulo, string Descricao, string Conteudo)>
        {
            (ModuloAgilean, 7, "Git & PR", "Padrão de Commits",
                "Convenção de commits (Conventional Commits) usada nos projetos.",
                """
                ## Padrão de Commits (Conventional Commits)
                Adotamos o padrão **Conventional Commits**, que ajuda a manter o histórico de
                alterações mais organizado, facilita automações como geração de changelog e melhora
                a legibilidade do repositório.

                ## Formato do commit
                ```
                <tipo>[escopo]: <mensagem breve em inglês>

                * texto maior se necessário
                ```
                Exemplo:
                ```
                feat(TASK-123): add endpoint to get packages
                ```

                ## Tipos de commit aceitos
                | Tipo | Quando usar |
                |---|---|
                | `feat` | Nova funcionalidade |
                | `fix` | Correção de bugs |
                | `chore` | Manutenção que não afeta a lógica (configs, scripts) |
                | `docs` | Alterações em documentação |
                | `style` | Formatação (indentação, ponto e vírgula) sem mudança lógica |
                | `refactor` | Refatoração que não altera o comportamento |
                | `test` | Adição ou modificação de testes |
                | `perf` | Melhorias de performance |
                | `build` | Scripts/build/configuração de dependências |
                | `ci` | Configurações de pipelines de CI/CD |

                ## Escopo (obrigatório)
                O escopo define onde a mudança aconteceu — task, módulo, pacote, componente etc.

                **Sempre** que houver card relacionado, o escopo é a task. Sem task, use outro
                escopo (ex.: `refactor(long-term): ...`).

                Se precisar de uma explicação maior, pule uma linha e escreva uma mensagem clara do
                que foi feito de fato no commit.

                Exemplos:
                - `feat(TASK-1000): ...`
                - `fix(BUG-123): ...`
                - `refactor(long-term): ...`

                ## Exemplo prático
                ```
                feat(TASK-1000): add package dates to long term table

                fix(BUG-123): submit button not triggering event

                refactor(long-term): remove scenario value as table dependency

                * To calculate the long-term table, it was necessary to calculate
                  the scenario values, and this was causing significant slowdowns.
                  This commit addresses this need and changes it to simply
                  querying the database, as the necessary data could be
                  calculated in a simple query, without the need for
                  distribution calculations.
                ```

                ## O que evitar
                - Commits genéricos como `update`, `ajustes`, `wip`, `small changes`, `fixes`.
                - Commits misturando vários tipos de alteração (ex.: feature + fix + refactor no
                  mesmo commit).
                - Mensagens sem contexto: "update screen", "improvements".
                """),
            (ModuloAgilean, 8, "Git & PR", "Regras de PR's",
                "Padrão de título, descrição e aprovação de Pull Requests.",
                """
                ## Padrão de Pull Requests (PRs)
                Pull Requests são essenciais para garantir qualidade de código, rastreabilidade e
                alinhamento entre o time. Seguir um padrão claro agiliza o processo e reduz
                retrabalho.

                ## Título do PR
                O título deve seguir o mesmo padrão dos commits:
                ```
                <tipo>(TASK-123): descricao breve do PR
                ```
                Use o mesmo tipo e escopo da branch, com um título claro e direto.

                Exemplos:
                - `feat(TASK-101): create login screen`
                - `fix(portal): create new employee`

                ## Descrição do PR
                Use o modelo abaixo sempre que abrir uma PR:
                ```
                ## O que foi feito*:

                - Pode replicar o que tiver nos commits, e se não tiver claro o suficiente,
                comentar melhor.

                ## Screenshots:

                [imagem]

                ## Checklist*

                - [x] Não deixei logs ou console
                - [x] Rodei os testes automatizados
                - [x] O código está seguindo os padrões de estilo e lint
                - [x] Revisei meu código

                ## Observações

                - Qualquer ponto de atenção que fizer sentido pra quem for revisar!
                ```

                ## Boas práticas
                - Escreva o PR como se fosse para outra pessoa entender rapidamente.
                - Use screenshots sempre que houver impacto visual e for útil pro entendimento.
                - Mencione outros devs ou tasks relacionadas, se necessário (`@dev`,
                  `Depende de TASK-456`).
                - Revise seu próprio código antes de pedir revisão.

                ## Regras de aprovação
                - Preencha as checklists.
                - Toda PR precisa de **pelo menos 1 aprovação de outro dev** (exceção para PRs com
                  tipo `style` ou pequenos `fix`).
                - PRs críticas devem ser revisadas por alguém responsável pelo domínio.
                - Nunca force push em branch de PR compartilhada sem avisar.

                ## Após o merge
                - Prefira **fast-forward** para manter um histórico mais limpo.
                - Apague a branch após o merge, salvo exceções onde ela ainda será usada.
                """),
            (ModuloMdO, 9, "Integrações", "Integração de Folha de Produção – Agilean x ERP",
                "Pré-requisitos, consultas SQL e fluxo operacional da integração Agilean x ERP.",
                """
                ## Integração de Folha de Produção – Agilean x ERP
                Procedimentos, requisitos técnicos e fluxos operacionais necessários para a
                integração entre a plataforma Agilean e o ERP, com foco na sincronização de dados
                de mão de obra e apropriação de custos via folha de produção.

                ## 1. Pré-requisitos e configurações obrigatórias (ERP + Agilean)
                Para garantir o funcionamento correto da comunicação entre os sistemas, os itens
                abaixo devem estar atendidos antes de iniciar a integração:

                **1.1 Vínculo de orçamento no ERP** — o cliente deve possuir um orçamento
                previamente criado e vinculado ao projeto no ERP.

                **1.2 Permissões de acesso** — o usuário configurado para a integração deve possuir
                permissões explícitas no banco do ERP para leitura (consultas), escrita
                (gravações/apropriações) e exclusão (quando aplicável aos fluxos da integração).

                **1.3 Vínculo de funcionário com insumo** — todo funcionário deve estar vinculado a
                um insumo no ERP. O Agilean precisa do **Insumo** no momento do envio das
                apropriações: todo funcionário deve estar vinculado a uma função, e toda função
                deve estar vinculada a um insumo. Sem essa configuração, o envio de apropriações
                não é possível.

                **1.4 Configuração de atividade extra** (obrigatória para apropriações fora do
                planejamento) — para apropriar custos de atividades extras (não previstas no
                planejamento), é necessário configurar no ERP uma atividade pai que servirá como
                âncora hierárquica. O código de recebimento de atividades extras deve estar sempre
                vinculado a essa atividade pai, respeitando o padrão hierárquico de atividades do
                ERP.

                Na prática: se no ERP já existe a atividade pai `9.99`, o Agilean cria
                automaticamente uma atividade filha `9.99.99` — usada no Agilean, com todo custo de
                atividade extra apropriado exclusivamente nela.

                ## 2. Configuração de consultas SQL (ERP)
                A integração depende de consultas específicas no SQL Server para extrair dados de
                funcionários e custos.

                ### 2.1 Importação de funcionários e funções
                Sincroniza o cadastro de pessoal e associa os insumos de mão de obra
                correspondentes:
                ```sql
                DECLARE @PRJ INT
                DECLARE @COL INT

                SET @COL = :COLIGADA
                SET @PRJ = :IDPRJ

                SELECT
                    PFUNC.CODCOLIGADA,
                    PFUNC.CHAPA,
                    PPESSOA.CPF,
                    PFUNC.NOME,
                    PFUNC.CODFUNCAO,
                    PFUNCAO.NOME AS FUNCAO,
                    PFUNC.SALARIO,
                    PFUNC.DATAADMISSAO,
                    PFUNC.DATADEMISSAO,
                    PPESSOA.DTNASCIMENTO,
                    PFUNC.CODSITUACAO,
                    PCODSITUACAO.DESCRICAO AS SITUACAO,
                    MISMFUNC.IDISM,
                    MISM.CODISM,
                    MISM.DESCISM AS INSUMO,
                    CASE WHEN MISM.CODUND IN ('H', 'UN') THEN 1 ELSE 0 END HORISTA
                FROM PFUNC
                INNER JOIN PPESSOA ON PFUNC.CODPESSOA = PPESSOA.CODIGO
                INNER JOIN PFUNCAO ON PFUNC.CODFUNCAO = PFUNCAO.CODIGO
                    AND PFUNC.CODCOLIGADA = PFUNCAO.CODCOLIGADA
                INNER JOIN PCODSITUACAO ON PFUNC.CODSITUACAO = PCODSITUACAO.CODCLIENTE
                LEFT JOIN MISMFUNC ON MISMFUNC.CODFUNCAO = PFUNCAO.CODIGO
                    AND MISMFUNC.CODCOLIGADA = PFUNCAO.CODCOLIGADA
                    AND MISMFUNC.IDPRJ = @PRJ
                LEFT JOIN MISM ON MISM.IDISM = MISMFUNC.IDISM
                    AND MISM.CODCOLIGADA = MISMFUNC.CODCOLIGADA
                    AND MISM.IDPRJ = MISMFUNC.IDPRJ
                WHERE PFUNC.CODCOLIGADA = @COL
                ```

                ### 2.2 Importação de custos de mão de obra
                O Agilean consome especificamente o grupo `GRUPODNER = 'B'` → **Mão de Obra**. É
                essencial que os insumos estejam corretamente classificados no ERP e que exista a
                curva S do projeto.
                ```sql
                SELECT
                    CURVS.CODCOLIGADA,
                    CURVS.IDPRJ,
                    CURVS.IDTRF,
                    MTAREFA.CODTRF,
                    MTAREFA.NOME AS TAREFA,
                    MTAREFA.QUANTIDADE QTD_TAREFA,
                    MTAREFA.VALOR VLR_TAREFA,
                    CURVS.IDISM,
                    MISM.CODISM,
                    MISM.DESCISM AS INSUMO,
                    MISM.VALOR VLR_INSUMO,
                    SUM((CURVS.PERCPLANEJADO / 100) * CURVS.QUANTPLANEJADO) AS QTD_PLANEJADO,
                    SUM((CURVS.PERCPLANEJADO / 100) * CURVS.VALORTOTAL) AS VLR_PLANEJADO,
                    CURVS.IDPERIODO,
                    MPERIODO.DTINICIO AS PERIODO_INICIO,
                    MPERIODO.DTFIM AS PERIODO_FIM,
                    MISM.GRUPODNER AS COD_GRUPO,
                    CASE MISM.GRUPODNER
                        WHEN 'A' THEN 'EQUIPAMENTO'
                        WHEN 'B' THEN 'MÃO DE OBRA'
                        WHEN 'C' THEN 'MATERIAL'
                        WHEN 'D' THEN 'ATIVIDADES AUXILIARES'
                        WHEN 'E' THEN 'TEMPO FIXO'
                        WHEN 'F' THEN 'MOMENTO DE TRANSPORTE'
                        WHEN 'N' THEN 'NENHUM'
                    END AS GRUPO
                FROM MCURVASISM (NOLOCK) CURVS --Tabela de curva S
                LEFT JOIN MTAREFA (NOLOCK) ON MTAREFA.IDTRF = CURVS.IDTRF
                    AND MTAREFA.CODCOLIGADA = CURVS.CODCOLIGADA
                    AND MTAREFA.IDPRJ = CURVS.IDPRJ
                LEFT JOIN MISM (NOLOCK) ON MISM.IDISM = CURVS.IDISM --tabela de insumos
                    AND MISM.CODCOLIGADA = CURVS.CODCOLIGADA
                    AND MISM.IDPRJ = CURVS.IDPRJ
                LEFT JOIN MPERIODO (NOLOCK) ON MPERIODO.CODCOLIGADA = CURVS.CODCOLIGADA
                    AND MPERIODO.IDPRJ = CURVS.IDPRJ
                    AND MPERIODO.IDPERIODO = CURVS.IDPERIODO
                WHERE CURVS.CODCOLIGADA = :COLIGADA
                  AND CURVS.IDPRJ = :IDPRJ
                GROUP BY
                    CURVS.CODCOLIGADA, CURVS.IDPRJ, CURVS.IDTRF, MTAREFA.QUANTIDADE,
                    MTAREFA.VALOR, CURVS.IDISM, MISM.VALOR, CURVS.IDPERIODO,
                    MTAREFA.CODTRF, MISM.CODISM, MISM.GRUPODNER, MTAREFA.NOME,
                    MISM.DESCISM, MPERIODO.DTINICIO, MPERIODO.DTFIM
                ORDER BY CURVS.IDPERIODO
                ```

                ### 2.3 Criação de tarefa (atividades extras)
                O Agilean precisa criar a tarefa onde serão apropriados os custos extras
                provenientes de verbas extras cadastradas no sistema. Para isso, é essencial que o
                ERP disponibilize um endpoint de criação de tarefa com retorno do ID da tarefa
                criada — usado posteriormente no envio das apropriações. O Agilean envia um objeto
                assim:
                ```csharp
                var mtTarefa = new MtTarefa
                {
                    Nome = "Tarefas Extras",
                    Descricao = "Tarefas Extras Agilean",
                    Ativa = 1,
                    CodColigada = companyId,
                    CodTrf = extraTaskCode,
                    IdPrj = budget.TotvsProjectId,
                    IdPrjRec = budget.TotvsProjectId
                };
                ```

                ### 2.4 Deleção de apropriações
                O Agilean precisa conseguir deletar as apropriações do ERP após o envio, permitindo
                reenvios quando necessário. Requisitos obrigatórios: endpoint de deleção de
                apropriações e retorno dos IDs das apropriações no momento do envio.

                **Cenário comum:** o cliente aprova a folha, a folha é enviada ao ERP, e
                posteriormente são identificados ajustes necessários. Nessa situação, o Agilean
                precisa deletar todas as apropriações enviadas, refazer o envio e garantir
                consistência entre Agilean e ERP.

                ## 3. Configuração dos endpoints (API ERP)
                Os endpoints devem seguir o padrão da API Framework do ERP.

                Funcionários:
                ```
                api/framework/v1/consultaSQLServer/RealizaConsulta/LABSQL0061/0/P/?parameters=COLIGADA%3D7
                ```
                Custos:
                ```
                api/framework/v1/consultaSQLServer/RealizaConsulta/CONST0030/0/M/
                ```
                No ERP (exemplo TOTVS), a tela "Editar Integração" concentra: sistema ERP, envio
                automático, senha da ERP, identificador da empresa, endereço de acesso para
                importar funcionários, endereço de acesso para importar valor de mão de obra, e o
                código de recebimento para atividades extras (ex.: `9.99.99`).

                ## 4. Tratamento de atividades extras (regra de apropriação)
                Quando houver atividade extra (fora do previsto), a apropriação deve seguir o
                modelo hierárquico configurado:
                - Atividade pai no ERP → `9.99`
                - Atividade filha criada pelo Agilean → `9.99.99`
                - Apropriação efetiva → sempre na atividade filha

                Resultado: todos os custos extras ficam centralizados, rastreáveis e controlados
                dentro do orçamento do ERP.

                ## 5. Fluxo operacional (passo a passo)
                1. **Preparação** — criar e validar consultas SQL no ERP.
                2. **Configuração** — preencher parâmetros de integração no portal Agilean.
                3. **Sincronização inicial** — importar funcionários e funções.
                4. **Planejamento** — atribuir tarefas aos colaboradores no Agilean.
                5. **Ciclo da folha** — gerar, validar e aprovar a Folha de Produção.
                6. **Finalização** — envio automático das apropriações ao ERP após aprovação.
                """),
        };

        return docs.Select(d => new Fluxo
        {
            Order = d.Order,
            ModuloId = moduloPorNome[d.Modulo],
            SquadId = squadPorNome[d.Modulo],
            Categoria = d.Categoria,
            Titulo = d.Titulo,
            Descricao = d.Descricao,
            Conteudo = d.Conteudo,
            VideoUrl = string.Empty,
            Tipo = TipoConteudo.Documentacao,
        });
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
        ["Visão geral do Quiz Quality"] = """
        ## Visão geral do Quiz Quality
        O módulo de **Quiz Quality (QQ)** controla a **qualidade das entregas** numa obra: o que
        precisa ser inspecionado, quem inspeciona em campo, e como cada não conformidade é
        tratada até ser resolvida.

        ## As três pontas
        - **Cadastro e preparação da obra** — o que existe para inspecionar (itens, checklists, mapas).
        - **Inspeções (App)** — o registro em campo, feito por quem está na obra.
        - **Qualidade Admin (Portal)** — a criação e tramitação das inspeções, normais e mapeadas.

        > **Ideia central:** cada inspeção nasce de um cadastro, é registrada em campo pelo App, e
        > tramita no Portal até virar uma ação de verdade. QQ é o que amarra *o que checar → quem
        > viu → o que foi feito*.

        Comece pelo **Cadastro e preparação da obra** (a base de tudo) e depois entenda como o
        **App** e o **Portal** se conversam no dia a dia.
        """,
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
        ["Visão geral do Agilean (desktop)"] = """
        ## Visão geral do Agilean (desktop)
        O **Agilean (desktop)** é o produto original de **Planejamento & Controle** de obra:
        onde o projeto é estruturado, o planejamento é criado, e o andamento real é medido
        contra o que foi planejado.

        ## As três pontas
        - **Estrutura** — empresa, filial, projeto, tipologia e diagrama (a base de tudo).
        - **Planejamento** — linha de balanço, linha base e orçamento.
        - **Acompanhamento** — reprogramação e medição, no desktop e pelo App Agilean, até o
          fechamento do período.

        > **Ideia central:** o diagrama vira linha de balanço, a linha de balanço vira orçamento,
        > e o acompanhamento (reprogramação + medição) mostra o quanto a obra real se afasta do
        > planejado.

        Comece pelos **Primeiros passos** (criar a estrutura) e depois siga para o **Planejamento**
        e **Acompanhamento**, que são o ciclo que se repete a cada período.
        """,
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
                Uma visão geral de como o sistema da Agilean é organizado — para você saber *onde*
                mexer antes de *como* mexer.

                ## Multi-repositório
                - **agilean_portal** — o front (React + TypeScript + Vite).
                - **api** — o back (C# / .NET).
                - **projects** e **contract** — entram como **submódulos** do `api` (que aponta para
                  um commit específico de cada; daí o "bump de submódulo").

                ## Back (C# / .NET)
                Organizado em **camadas** e no estilo **CQRS** (comandos escrevem, queries leem):
                - **Query/Command** → **Handler** (a regra) → **Repository** (dados via **Dapper**).
                - O banco devolve uma **View** (projeção SQL), que o **AutoMapper** converte na
                  **Response** (o DTO que sai para a API). Fluxo mental: *View (banco) → AutoMapper →
                  Response (API)*.
                - Regra de negócio mora no domínio/handler, **não** no controller.

                ## Front (React + TS)
                - **Design System** próprio em `src/agilean-design-system` — reusar antes de criar.
                - **Estilo:** Tailwind com tokens **`ads-*`** (tema troca sozinho); sem CSS custom.
                - **Estado:** **Zustand** (sessão/obra) + **TanStack React Query** (dados do servidor).
                - **Tabelas:** **AgGrid Enterprise v31** (locale PT) para as grids pesadas.
                - **Padrão de tela:** `context/` (estado) + `services/` (chamadas tipadas que
                  retornam `Result<T>` via `api()`) + hooks (`useColumns`, `useNomeDoHook`).

                ## Como se conectam
                O front chama a API por HTTP; a API lê/escreve via Dapper e devolve Responses. Front
                e back são **desacoplados** — dá para evoluir a tela com mock e, na integração, trocar
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

                > Nada de placeholder — o PR é real e vai para review.
                """,
            },
            new()
            {
                Categoria = "Git & PR",
                Titulo = "Rebase no support",
                Descricao = "Trazer sua branch para o topo do support antes de subir.",
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
                Descricao = "Apontar o api para o novo commit do submódulo.",
                Conteudo = """
                ## Bump de submódulo
                Quando você commita em `projects`/`contract`, o `api` precisa apontar para o novo commit:
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
            // Básico do dev é referência escrita (arquitetura, PR, rebase...), nunca vídeo — Fluxo
            // é reservado pra conteúdo em vídeo mostrando o sistema (ver Tipo em Fluxo.cs).
            fluxo.Tipo = TipoConteudo.Documentacao;
        }
        return basicos;
    }
}
