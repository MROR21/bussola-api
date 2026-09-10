using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Bussola.Api.Auth;
using Bussola.Api.Services;
using Bussola.Domain.Entities;
using Bussola.Domain.Nivelamento;
using Bussola.Domain.ValueObjects;
using Bussola.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Swagger — documentação interativa da API
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Banco (Postgres via EF Core)
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// Serializa enums como texto no JSON (ex.: "Git" em vez de 3) — deixa a API auto-documentada.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// CORS liberando o front (Vite dev)
const string FrontCors = "front";
builder.Services.AddCors(options =>
    options.AddPolicy(FrontCors, policy =>
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()));

// Emissor de JWT (login demo — token com expiração).
builder.Services.AddSingleton<TokenService>();

// Entrega dos eventos no Teams (Incoming Webhook). Sem URL = no-op/mock.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TeamsNotifier>();

// Confirmação de e-mail no cadastro por senha (código de 6 dígitos). Sem credencial = no-op/mock.
builder.Services.AddSingleton<EmailSender>();

// Validação do JWT (Auth B): protege os endpoints do gestor. O front manda o Bearer token.
var jwtKey = builder.Configuration["Jwt:Key"]!;
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = "SmartAuth";
        options.DefaultAuthenticateScheme = "SmartAuth";
        options.DefaultChallengeScheme = "SmartAuth";
    })
    // Dá pra chamar a API com um token pessoal (gerado em /perfil/api-tokens) no lugar do login
    // normal — mesmo uso que já se faz com token do Jira/Bitbucket, direto via curl/PowerShell
    // sem passar pelo login. Detecta pelo PREFIXO do valor (só o token de API começa com
    // "bussola_pat_") e despacha pro handler certo — o resto da API (policies, endpoints) nem
    // sabe qual dos dois autenticou, já que os dois produzem os mesmos claims (sub/gestor/nome).
    .AddPolicyScheme("SmartAuth", "JWT ou token de API", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer " + ApiTokenHasher.Prefixo, StringComparison.Ordinal)
                ? ApiTokenAuthHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthHandler>(ApiTokenAuthHandler.SchemeName, _ => { })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // mantém "sub"/"gestor" com o nome original
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtIssuer,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
        };
        // Sem isso, alguém com o acesso revogado (Ativo=false) continuava usando o token já
        // emitido normalmente até ele expirar sozinho — a checagem de `Ativo` só existia no LOGIN,
        // nunca em requests de uma sessão já aberta. Aqui confere o banco a cada request
        // autenticado; revogado = falha a autenticação com um motivo próprio (não confundir com
        // token expirado/inválido), que o OnChallenge abaixo transforma numa mensagem real no
        // corpo da resposta — o front (`services/api.ts`) já lê `erro` do corpo em qualquer 401.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                if (!Guid.TryParse(context.Principal?.FindFirstValue("sub"), out var userId))
                {
                    context.Fail("token-sem-sub");
                    return;
                }
                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var usuario = await db.Usuarios
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.Ativo, u.IsGestor })
                    .FirstOrDefaultAsync();
                if (usuario is null || !usuario.Ativo)
                {
                    context.Fail("acesso-revogado");
                    return;
                }
                // O claim "gestor" gravado no token pode estar velho (token dura até 24h) — sem
                // isso, promover/demover alguém só refletia depois de relogar (a policy "Gestor"
                // só olhava o claim do token, nunca revalidava contra o banco). Reconstrói o claim
                // a cada request com o valor atual, mesmo padrão já usado acima pra `Ativo`.
                if (context.Principal!.Identity is ClaimsIdentity identity)
                {
                    foreach (var antigo in identity.FindAll("gestor").ToList())
                    {
                        identity.RemoveClaim(antigo);
                    }
                    identity.AddClaim(new Claim("gestor", usuario.IsGestor ? "true" : "false"));
                }
            },
            OnChallenge = async context =>
            {
                if (context.AuthenticateFailure?.Message == "acesso-revogado")
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new { erro = "Seu acesso foi revogado." });
                }
            },
        };
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("Gestor", policy => policy.RequireClaim("gestor", "true")));

var app = builder.Build();

// TeamsNotifier é singleton → resolvo uma vez e uso nos eventos (evita injetar em cada endpoint).
var teams = app.Services.GetRequiredService<TeamsNotifier>();

// Compartilhado entre ConcluirPasso (envio normal) e ConfirmarCorrecaoPasso (aprovação do gestor)
// — os dois são pontos onde um passo pode passar a contar como "concluído de verdade" (ver
// PrecisaCorrecao/AguardandoConfirmacao) e por isso fechar a fase inteira. `db` vem por parâmetro
// (cada endpoint recebe o seu, injetado por request); `teams` é capturado do escopo de fora.
async Task NotificarSeFaseCompletaAsync(
    AppDbContext db, Guid colaboradorId, string nomeColaborador, Guid gestorId, Guid stepId, string evidencia)
{
    var step = await db.OnboardingSteps.Include(s => s.Fase).FirstOrDefaultAsync(s => s.Id == stepId);
    if (step is null) return;

    var idsDaFase = await db.OnboardingSteps
        .Where(s => s.FaseId == step.FaseId)
        .Select(s => s.Id)
        .ToListAsync();
    var concluidosDaFase = await db.PassosConcluidos
        .CountAsync(p => p.UsuarioId == colaboradorId && idsDaFase.Contains(p.OnboardingStepId)
            && !p.PrecisaCorrecao && !p.AguardandoConfirmacao);

    if (idsDaFase.Count == 0 || concluidosDaFase < idsDaFase.Count) return;

    var ehPrimeiroCard = step.Fase.Nome == "Primeiro Card";
    var msg = $"{nomeColaborador} concluiu a fase {step.Fase.Nome}.";
    db.Notificacoes.Add(new Notificacao
    {
        UsuarioId = gestorId,
        Mensagem = msg,
        AutorId = colaboradorId,
        // Na conclusão do Primeiro Card, leva pra tela do supervisionado (não direto pro link do
        // PR) — lá o gestor já vê a comprovação junto do bloco "Primeiro card" (decisão do Miguel:
        // manter o PR dentro do contexto da pessoa, não abrir direto pra fora do Bússola).
        // `?destaque=primeiro-card` abre o dropdown certo sozinho e destaca ele na tela.
        Link = ehPrimeiroCard ? $"/supervisionado/{colaboradorId}?destaque=primeiro-card" : string.Empty,
    });
    await db.SaveChangesAsync();

    // Teams fica só pro marco de liberar o Primeiro Card (decisão do Miguel 2026-09-05) — as
    // demais fases avisam só no sino, sem barulho no Teams.
    if (ehPrimeiroCard)
    {
        var msgComLink = !string.IsNullOrWhiteSpace(evidencia) ? $"{msg} PR: {evidencia}" : msg;
        await teams.EnviarAsync(msgComLink);
    }

    // Aviso SEPARADO (sino + Teams) quando a fase concluída agora é a que vem JUSTO ANTES do
    // Primeiro Card na ordem cadastrada — sinal de "chegou a hora de escolher e mandar um card pra
    // essa pessoa". Não dispara quando quem chamou já É o Primeiro Card (não tem "próxima fase"
    // relevante nesse caso) — a checagem abaixo já cobre isso sozinha.
    var fasesOrdenadas = await db.Fases.OrderBy(f => f.Order).ToListAsync();
    var indiceAtual = fasesOrdenadas.FindIndex(f => f.Id == step.FaseId);
    var proximaFase = indiceAtual >= 0 && indiceAtual + 1 < fasesOrdenadas.Count
        ? fasesOrdenadas[indiceAtual + 1]
        : null;
    if (proximaFase?.Nome == "Primeiro Card")
    {
        var msgChegou = $"{nomeColaborador} chegou na fase Primeiro Card! Hora de escolher um card pra ele(a).";
        db.Notificacoes.Add(new Notificacao
        {
            UsuarioId = gestorId,
            Mensagem = msgChegou,
            AutorId = colaboradorId,
            // Mesmo destino das outras notificações do Primeiro Card — é lá que o gestor manda o
            // link do card pro supervisionado (`?destaque=primeiro-card` já abre e destaca o
            // bloco certo). Sem isso, a notificação avisava mas não levava a pessoa pra ação.
            Link = $"/supervisionado/{colaboradorId}?destaque=primeiro-card",
        });
        await db.SaveChangesAsync();
        await teams.EnviarAsync(msgChegou);
    }
}

// Ao iniciar: aplica migrations pendentes e semeia os dados iniciais (dev).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await OnboardingSeeder.SeedAsync(db);
    await FluxoSeeder.SeedAsync(db);
    await AcessoSeeder.SeedAsync(db);

    // Backfill: notificações antigas de "liberou o fluxo" (sem link) ganham o redirecionamento.
    const string prefixoLiberou = "Seu gestor liberou o fluxo: ";
    var semLink = await db.Notificacoes
        .Where(n => n.Link == "" && n.Mensagem.StartsWith(prefixoLiberou))
        .ToListAsync();
    if (semLink.Count > 0)
    {
        var idPorTitulo = (await db.Fluxos.ToListAsync())
            .GroupBy(f => f.Titulo)
            .ToDictionary(g => g.Key, g => g.First().Id);
        foreach (var n in semLink)
        {
            var titulo = n.Mensagem[prefixoLiberou.Length..].TrimEnd('.', ' ');
            if (idPorTitulo.TryGetValue(titulo, out var fid))
            {
                n.Link = $"/fluxos?destaque={fid}";
            }
        }
        await db.SaveChangesAsync();
    }
}

// Rede de segurança: qualquer exceção não-tratada vira um 500 { erro } limpo (sem stack pro
// cliente). Em dev, inclui a mensagem real pra facilitar o debug; em prod, mensagem genérica.
app.UseExceptionHandler(handler =>
    handler.Run(async context =>
    {
        var falha = context.Features.Get<IExceptionHandlerFeature>();
        var detalhe = app.Environment.IsDevelopment() ? falha?.Error.Message : null;
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { erro = detalhe ?? "Erro interno no servidor." });
    }));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(FrontCors);
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "bussola-api" }))
   .WithName("Health");

// Fases com significado próprio na trilha: a do squad (montada a partir dos fluxos, não semeada
// como passo) e a final, que só libera quando todo o resto está concluído.
const string FaseConhecaOSistema = "Conheça o sistema";
const string FasePrimeiroCard = "Primeiro Card";

// Lista os passos de onboarding, ordenados. `Phase` é projetada a partir da entidade Fase (FK) —
// mantém o mesmo formato de resposta de sempre pro front, mesmo com o modelo normalizado por baixo.
// Ordena por Fase.Order primeiro (é o que as setas ▲▼ do Admin mexem) e só depois pelo Order do
// próprio passo dentro da fase — reordenar Fase no Admin muda de verdade a sequência da Jornada.
app.MapGet("/onboarding/steps", async (AppDbContext db) =>
    await db.OnboardingSteps
        .OrderBy(step => step.Fase.Order)
        .ThenBy(step => step.Order)
        .Select(step => new
        {
            step.Id,
            step.Order,
            Phase = step.Fase.Nome,
            step.Title,
            step.Description,
            step.IsCompanySpecific,
            step.SkillArea,
            step.Conteudo,
            step.VideoUrl,
        })
        .ToListAsync())
   .WithName("GetOnboardingSteps");

// Monta a trilha do usuário logado: os passos (com a profundidade recomendada) MAIS os fluxos do
// squad dele, como uma fase própria logo antes do Primeiro Card. Conhecer o sistema do squad é
// parte do onboarding; depois de concluído, esses mesmos fluxos seguem acessíveis no Guia pelo
// sistema (que é aberto a todos). O item traz `Tipo` pro front saber se navega pro passo ou pro fluxo.
app.MapPost("/onboarding/trail", async (Perfil perfil, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var usuario = await db.Usuarios.FindAsync(userId);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });

    var steps = await db.OnboardingSteps.Include(step => step.Fase)
        .OrderBy(step => step.Fase.Order).ThenBy(step => step.Order).ToListAsync();
    var fluxosDoSquad = await db.Fluxos
        .Where(fluxo => fluxo.Squad == usuario.Squad)
        .OrderBy(fluxo => fluxo.Order)
        .ToListAsync();

    var trail = new List<TrailItemView>();

    void AdicionarFluxosDoSquad() => trail.AddRange(fluxosDoSquad.Select(fluxo => new TrailItemView(
        fluxo.Id, fluxo.Order, FaseConhecaOSistema, fluxo.Titulo, fluxo.Descricao,
        true, SkillArea.None, fluxo.Conteudo, StepDepth.Essencial, "fluxo")));

    var inseriuFluxos = false;
    foreach (var step in steps)
    {
        if (!inseriuFluxos && step.Fase.Nome == FasePrimeiroCard)
        {
            AdicionarFluxosDoSquad();
            inseriuFluxos = true;
        }

        trail.Add(new TrailItemView(
            step.Id, step.Order, step.Fase.Nome, step.Title, step.Description,
            step.IsCompanySpecific, step.SkillArea, step.Conteudo,
            TrailPlanner.DepthFor(step, perfil), "passo"));
    }

    // Sem a fase do Primeiro Card (base customizada), os fluxos entram no fim.
    if (!inseriuFluxos) AdicionarFluxosDoSquad();

    return Results.Ok(trail);
})
   .WithName("GetOnboardingTrail")
   .RequireAuthorization();

// Um passo específico (com o conteúdo em Markdown). Usado na página de detalhe do passo.
app.MapGet("/onboarding/steps/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var step = await db.OnboardingSteps
        .Where(s => s.Id == id)
        .Select(s => new
        {
            s.Id,
            s.Order,
            Phase = s.Fase.Nome,
            s.Title,
            s.Description,
            s.IsCompanySpecific,
            s.SkillArea,
            s.Conteudo,
            s.VideoUrl,
        })
        .FirstOrDefaultAsync();
    return step is null
        ? Results.NotFound(new { erro = "Passo não encontrado." })
        : Results.Ok(step);
})
   .WithName("GetOnboardingStep");

// --- Fluxos (Referência viva) ---

// Lista todos os fluxos, ordenados. O Guia pelo sistema é aberto a QUALQUER colaborador logado —
// não filtra por squad nem por atribuição (decisão de produto: o repositório é de todos; o que é
// específico do squad entra na jornada, não como restrição de acesso).
app.MapGet("/fluxos", async (AppDbContext db) =>
    await db.Fluxos
        .OrderBy(fluxo => fluxo.Modulo.Order)
        .ThenBy(fluxo => fluxo.Order)
        .Select(fluxo => new
        {
            fluxo.Id,
            fluxo.Order,
            Modulo = fluxo.Modulo.Nome,
            fluxo.Squad,
            fluxo.Categoria,
            fluxo.Titulo,
            fluxo.Descricao,
            fluxo.Conteudo,
            fluxo.VideoUrl,
        })
        .ToListAsync())
   .WithName("GetFluxos")
   .RequireAuthorization();

// Um fluxo específico (com o conteúdo em Markdown).
app.MapGet("/fluxos/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var fluxo = await db.Fluxos
        .Where(f => f.Id == id)
        .Select(f => new
        {
            f.Id,
            f.Order,
            Modulo = f.Modulo.Nome,
            f.Squad,
            f.Categoria,
            f.Titulo,
            f.Descricao,
            f.Conteudo,
            f.VideoUrl,
        })
        .FirstOrDefaultAsync();
    return fluxo is null
        ? Results.NotFound(new { erro = "Fluxo não encontrado." })
        : Results.Ok(fluxo);
})
   .WithName("GetFluxo");

// Ids dos fluxos que o usuário logado já concluiu.
app.MapGet("/fluxos/concluidos", async (ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var ids = await db.FluxosConcluidos
        .Where(f => f.UsuarioId == userId)
        .Select(f => f.FluxoId)
        .ToListAsync();
    return Results.Ok(ids);
})
   .WithName("GetFluxosConcluidos")
   .RequireAuthorization();

// Marca um fluxo como concluído (idempotente) + notifica o gestor, se houver.
app.MapPost("/fluxos/{fluxoId:guid}/concluir", async (Guid fluxoId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var ja = await db.FluxosConcluidos.AnyAsync(f => f.UsuarioId == userId && f.FluxoId == fluxoId);
    if (!ja)
    {
        db.FluxosConcluidos.Add(new FluxoConcluido { UsuarioId = userId, FluxoId = fluxoId });
        await db.SaveChangesAsync();

        // Só avisa o gestor (só no sino — Teams fica reservado pro Primeiro Card, ver endpoint
        // de progresso) quando o MÓDULO inteiro (fluxos visíveis) fecha.
        var usuario = await db.Usuarios.FindAsync(userId);
        if (usuario?.GestorId is Guid gestorId)
        {
            var fluxo = await db.Fluxos.Include(f => f.Modulo).FirstOrDefaultAsync(f => f.Id == fluxoId);
            if (fluxo is not null)
            {
                // O módulo inteiro = todos os fluxos dele (o guia é aberto, não há mais recorte
                // por squad/atribuição).
                var idsDoModulo = await db.Fluxos
                    .Where(f => f.ModuloId == fluxo.ModuloId)
                    .Select(f => f.Id)
                    .ToListAsync();
                var concluidosDoModulo = await db.FluxosConcluidos
                    .CountAsync(f => f.UsuarioId == userId && idsDoModulo.Contains(f.FluxoId));

                if (idsDoModulo.Count > 0 && concluidosDoModulo >= idsDoModulo.Count)
                {
                    // Teams fica só pro marco de liberar o Primeiro Card (decisão do Miguel
                    // 2026-09-05) — conclusão de módulo do Guia continua avisando só no sino.
                    var msg = $"{usuario.Nome} concluiu o módulo {fluxo.Modulo.Nome}.";
                    db.Notificacoes.Add(new Notificacao { UsuarioId = gestorId, Mensagem = msg, AutorId = userId });
                    await db.SaveChangesAsync();
                }
            }
        }
    }

    return Results.NoContent();
})
   .WithName("ConcluirFluxo")
   .RequireAuthorization();

// Desmarca um fluxo concluído.
app.MapDelete("/fluxos/{fluxoId:guid}/concluir", async (Guid fluxoId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var registro = await db.FluxosConcluidos
        .FirstOrDefaultAsync(f => f.UsuarioId == userId && f.FluxoId == fluxoId);
    if (registro is not null)
    {
        db.FluxosConcluidos.Remove(registro);
        await db.SaveChangesAsync();
    }

    return Results.NoContent();
})
   .WithName("DesmarcarFluxo")
   .RequireAuthorization();

// --- Gestor (protegido pela policy "Gestor") ---

// Lista os SUPERVISIONADOS do gestor logado, com o progresso de cada um.
app.MapGet("/gestor/usuarios", async (ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var totalPassos = await db.OnboardingSteps.CountAsync();
    // Estrito (aprovado de verdade, não só enviado) — pedido do Miguel pra manter coerência: essa
    // lista é a visão de "quanto cada supervisionado JÁ terminou de verdade", não um indicador de
    // progresso ao vivo tipo a barra da Jornada dele (que sim sente o envio como avanço parcial).
    var concluidosPorUsuario = await db.PassosConcluidos
        .Where(passo => !passo.PrecisaCorrecao && !passo.AguardandoConfirmacao)
        .GroupBy(passo => passo.UsuarioId)
        .Select(grupo => new { UsuarioId = grupo.Key, Total = grupo.Count() })
        .ToDictionaryAsync(x => x.UsuarioId, x => x.Total);

    // Acesso revogado (`Ativo=false`) some da lista de supervisionados — mas NÃO desvincula
    // `GestorId`, então reativar o acesso (em /admin/usuarios) já traz a pessoa de volta pra cá,
    // pro mesmo gestor, sem precisar readicionar como supervisionado.
    var usuarios = await db.Usuarios
        .Where(u => u.GestorId == gestorId && u.Ativo)
        .OrderBy(u => u.Nome)
        .ToListAsync();

    // Os fluxos do squad de cada um também contam como parte da Jornada (fase "Conheça o
    // sistema") — mesma regra que a trilha do próprio usuário e o progresso individual
    // (GET .../progresso) já seguem. Sem isso, o total aqui ficava menor que o real.
    var idsUsuarios = usuarios.Select(u => u.Id).ToList();
    var fluxosConcluidosPorUsuario = (await db.FluxosConcluidos
            .Where(f => idsUsuarios.Contains(f.UsuarioId))
            .ToListAsync())
        .GroupBy(f => f.UsuarioId)
        .ToDictionary(g => g.Key, g => g.Select(f => f.FluxoId).ToHashSet());
    var fluxosPorSquad = (await db.Fluxos.Where(f => f.Squad != null).ToListAsync())
        .GroupBy(f => f.Squad!.Value)
        .ToDictionary(g => g.Key, g => g.Select(f => f.Id).ToList());

    // Projeção em memória: Email é Value Object (não dá pra projetar .Value no SQL).
    var resultado = usuarios.Select(u =>
    {
        var fluxosDoSquad = fluxosPorSquad.GetValueOrDefault(u.Squad, new List<Guid>());
        var concluidosFluxo = fluxosConcluidosPorUsuario.GetValueOrDefault(u.Id, new HashSet<Guid>());
        var fluxosFeitos = fluxosDoSquad.Count(concluidosFluxo.Contains);

        return new
        {
            u.Id,
            u.Nome,
            Email = u.Email.Value,
            u.Cargo,
            u.Squad,
            u.IsGestor,
            u.NivelamentoConcluido,
            PassosConcluidos = concluidosPorUsuario.GetValueOrDefault(u.Id, 0) + fluxosFeitos,
            TotalPassos = totalPassos + fluxosDoSquad.Count,
            u.Foto,
        };
    });

    return Results.Ok(resultado);
})
   .WithName("GetGestorUsuarios")
   .RequireAuthorization("Gestor");

// Progresso passo-a-passo de um supervisionado (só do gestor dono dele).
app.MapGet("/gestor/usuarios/{usuarioId:guid}/progresso", async (Guid usuarioId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var alvo = await db.Usuarios.FindAsync(usuarioId);
    if (alvo is null || alvo.GestorId != gestorId)
    {
        return Results.NotFound(new { erro = "Supervisionado não encontrado." });
    }

    var registros = await db.PassosConcluidos
        .Where(p => p.UsuarioId == usuarioId)
        .ToListAsync();
    var evidenciaPorStep = registros.ToDictionary(p => p.OnboardingStepId, p => p.Evidencia);
    var precisaCorrecaoPorStep = registros.ToDictionary(p => p.OnboardingStepId, p => p.PrecisaCorrecao);
    var qtdCorrecoesPorStep = registros.ToDictionary(p => p.OnboardingStepId, p => p.QtdCorrecoes);
    var aguardandoConfirmacaoPorStep = registros.ToDictionary(p => p.OnboardingStepId, p => p.AguardandoConfirmacao);

    var steps = await db.OnboardingSteps.Include(s => s.Fase)
        .OrderBy(s => s.Fase.Order).ThenBy(s => s.Order).ToListAsync();

    // Mesma fase sintética "Conheça o sistema" que a trilha do próprio usuário monta (fluxos do
    // squad dele, logo antes do Primeiro Card) — sem isso, o gestor via só os Passos "de verdade"
    // e a contagem/trilha ficava incompleta (faltava uma fase inteira comparado à Jornada real).
    var fluxosConcluidos = (await db.FluxosConcluidos
        .Where(f => f.UsuarioId == usuarioId).Select(f => f.FluxoId).ToListAsync()).ToHashSet();
    var fluxosDoSquad = await db.Fluxos
        .Where(fluxo => fluxo.Squad == alvo.Squad)
        .OrderBy(fluxo => fluxo.Order)
        .ToListAsync();

    var passos = new List<object>();

    void AdicionarFluxosDoSquad() => passos.AddRange(fluxosDoSquad.Select(fluxo => (object)new
    {
        fluxo.Id,
        fluxo.Order,
        Phase = FaseConhecaOSistema,
        Title = fluxo.Titulo,
        Concluido = fluxosConcluidos.Contains(fluxo.Id),
        Evidencia = string.Empty,
        PrecisaCorrecao = false,
        QtdCorrecoes = 0,
        AguardandoConfirmacao = false,
    }));

    var inseriuFluxos = false;
    foreach (var s in steps)
    {
        if (!inseriuFluxos && s.Fase.Nome == FasePrimeiroCard)
        {
            AdicionarFluxosDoSquad();
            inseriuFluxos = true;
        }

        passos.Add(new
        {
            s.Id,
            s.Order,
            Phase = s.Fase.Nome,
            s.Title,
            // `Concluido` aqui é "tem registro" (mesmo critério de sempre) — é o que faz a barra
            // de progresso sentir o envio da comprovação como avanço (e o cancelamento como
            // retrocesso). Pra saber se foi APROVADO de verdade (não só enviado), quem usa combina
            // isso com `PrecisaCorrecao`/`AguardandoConfirmacao` abaixo — só os dois `false` com
            // `Concluido` true é aprovação de verdade.
            Concluido = evidenciaPorStep.ContainsKey(s.Id),
            Evidencia = evidenciaPorStep.GetValueOrDefault(s.Id, string.Empty),
            PrecisaCorrecao = precisaCorrecaoPorStep.GetValueOrDefault(s.Id, false),
            QtdCorrecoes = qtdCorrecoesPorStep.GetValueOrDefault(s.Id, 0),
            AguardandoConfirmacao = aguardandoConfirmacaoPorStep.GetValueOrDefault(s.Id, false),
        });
    }

    if (!inseriuFluxos) AdicionarFluxosDoSquad();

    return Results.Ok(new { alvo.Nome, alvo.Cargo, Passos = passos });
})
   .WithName("GetProgressoSupervisionado")
   .RequireAuthorization("Gestor");

// Todos os fluxos do guia com a flag de concluído do supervisionado. `DoSquad` marca os que fazem
// parte do onboarding dele (os do squad); o resto é consulta livre.
app.MapGet("/gestor/usuarios/{usuarioId:guid}/fluxos", async (Guid usuarioId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var alvo = await db.Usuarios.FindAsync(usuarioId);
    if (alvo is null || alvo.GestorId != gestorId)
    {
        return Results.NotFound(new { erro = "Supervisionado não encontrado." });
    }

    var concluidos = (await db.FluxosConcluidos
        .Where(f => f.UsuarioId == usuarioId).Select(f => f.FluxoId).ToListAsync()).ToHashSet();

    var todos = await db.Fluxos.Include(f => f.Modulo)
        .OrderBy(f => f.Modulo.Order).ThenBy(f => f.Order).ToListAsync();
    var visiveis = todos
        .Select(f => new
        {
            f.Id,
            f.Titulo,
            Modulo = f.Modulo.Nome,
            Concluido = concluidos.Contains(f.Id),
            DoSquad = f.Squad == alvo.Squad,
        });

    return Results.Ok(visiveis);
})
   .WithName("GetFluxosSupervisionado")
   .RequireAuthorization("Gestor");

// Acessos a liberar pro supervisionado, conforme o Cargo dele (cumulativo — ver comentário na
// entidade `Acesso`) + quais já foram marcados concluídos.
app.MapGet("/gestor/usuarios/{usuarioId:guid}/acessos", async (Guid usuarioId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var alvo = await db.Usuarios.FindAsync(usuarioId);
    if (alvo is null || alvo.GestorId != gestorId)
    {
        return Results.NotFound(new { erro = "Supervisionado não encontrado." });
    }

    var concluidos = (await db.AcessosConcluidos
        .Where(a => a.UsuarioId == usuarioId).Select(a => a.AcessoId).ToListAsync()).ToHashSet();

    var acessos = await db.Acessos
        .Where(a => a.CargoMinimo <= alvo.Cargo)
        .OrderBy(a => a.Order)
        .Select(a => new
        {
            a.Id,
            a.Nome,
            a.Link,
            Concluido = concluidos.Contains(a.Id),
        })
        .ToListAsync();

    return Results.Ok(acessos);
})
   .WithName("GetAcessosSupervisionado")
   .RequireAuthorization("Gestor");

// Marca (ou desmarca) um Acesso como liberado pro supervisionado — sempre uma ação do gestor DELE,
// clicando no chip (não existe callback de "voltou do link externo": marca já no clique).
app.MapPut("/gestor/usuarios/{usuarioId:guid}/acessos/{acessoId:guid}", async (
    Guid usuarioId, Guid acessoId, MarcarAcessoRequest req, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var alvo = await db.Usuarios.FindAsync(usuarioId);
    if (alvo is null || alvo.GestorId != gestorId)
    {
        return Results.NotFound(new { erro = "Supervisionado não encontrado." });
    }

    var existente = await db.AcessosConcluidos
        .FirstOrDefaultAsync(a => a.UsuarioId == usuarioId && a.AcessoId == acessoId);

    if (req.Concluido && existente is null)
    {
        db.AcessosConcluidos.Add(new AcessoConcluido { UsuarioId = usuarioId, AcessoId = acessoId });

        // Só notifica ao LIBERAR (não ao desmarcar) — o colaborador vê o chip virar verde na
        // própria Jornada (seção "Seus acessos"), o sino é só o aviso de que aconteceu.
        var acesso = await db.Acessos.FindAsync(acessoId);
        if (acesso is not null)
        {
            var gestorNome = user.FindFirstValue("nome") ?? "Seu gestor";
            db.Notificacoes.Add(new Notificacao
            {
                UsuarioId = usuarioId,
                Mensagem = $"{gestorNome} liberou seu acesso: {acesso.Nome}.",
                AutorId = gestorId,
            });
        }
    }
    else if (!req.Concluido && existente is not null)
    {
        db.AcessosConcluidos.Remove(existente);
    }

    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("MarcarAcessoSupervisionado")
   .RequireAuthorization("Gestor");

// Link do card que o gestor escolheu pro supervisionado (fase "Primeiro Card") — null enquanto
// não enviado ainda (é o que trava os passos da fase, ver GET /users/{id}/card-link).
app.MapGet("/gestor/usuarios/{usuarioId:guid}/card-link", async (Guid usuarioId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var alvo = await db.Usuarios.FindAsync(usuarioId);
    if (alvo is null || alvo.GestorId != gestorId)
    {
        return Results.NotFound(new { erro = "Supervisionado não encontrado." });
    }

    var cardLink = await db.CardLinks.FirstOrDefaultAsync(c => c.UsuarioId == usuarioId);
    return Results.Ok(new { Url = cardLink?.Url });
})
   .WithName("GetCardLinkSupervisionado")
   .RequireAuthorization("Gestor");

// Envia (ou reenvia/sobrescreve — 1 registro por pessoa, sem histórico) o link do card pro
// supervisionado. Notifica ele com um link CLICÁVEL de rota interna (o sino já sabe navegar,
// nenhuma mudança precisa no front pra esse caso — diferente do link externo do PR).
app.MapPut("/gestor/usuarios/{usuarioId:guid}/card-link", async (
    Guid usuarioId, CardLinkRequest req, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var url = req.Url?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(url))
    {
        return Results.BadRequest(new { erro = "Informe o link do card." });
    }

    var alvo = await db.Usuarios.FindAsync(usuarioId);
    if (alvo is null || alvo.GestorId != gestorId)
    {
        return Results.NotFound(new { erro = "Supervisionado não encontrado." });
    }

    var existente = await db.CardLinks.FirstOrDefaultAsync(c => c.UsuarioId == usuarioId);
    if (existente is null)
    {
        db.CardLinks.Add(new CardLink { UsuarioId = usuarioId, EnviadoPorGestorId = gestorId, Url = url });
    }
    else
    {
        existente.Url = url;
        existente.EnviadoPorGestorId = gestorId;
        existente.EnviadoEm = DateTime.UtcNow;
    }

    var gestorNome = user.FindFirstValue("nome") ?? "Seu gestor";
    db.Notificacoes.Add(new Notificacao
    {
        UsuarioId = usuarioId,
        Mensagem = $"{gestorNome} liberou seu primeiro card!",
        AutorId = gestorId,
        Link = "/fase/" + Uri.EscapeDataString("Primeiro Card"),
    });

    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("EnviarCardLink")
   .RequireAuthorization("Gestor");

// Colaboradores disponíveis pra virar supervisionado (ainda sem gestor).
app.MapGet("/gestor/disponiveis", async (AppDbContext db) =>
{
    var usuarios = await db.Usuarios
        .Where(u => !u.IsGestor && u.GestorId == null)
        .OrderBy(u => u.Nome)
        .ToListAsync();

    return Results.Ok(usuarios.Select(u => new { u.Id, u.Nome, Email = u.Email.Value, u.Cargo }));
})
   .WithName("GetGestorDisponiveis")
   .RequireAuthorization("Gestor");

// Associa um usuário como supervisionado do gestor logado.
app.MapPost("/gestor/supervisionados/{usuarioId:guid}", async (Guid usuarioId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var usuario = await db.Usuarios.FindAsync(usuarioId);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });
    if (usuario.IsGestor) return Results.BadRequest(new { erro = "Não dá pra supervisionar um gestor." });

    usuario.GestorId = gestorId;
    var gestorNome = user.FindFirstValue("nome") ?? "Seu gestor";
    db.Notificacoes.Add(new Notificacao
    {
        UsuarioId = usuarioId,
        Mensagem = $"{gestorNome} adicionou você como supervisionado.",
        AutorId = gestorId,
    });
    await db.SaveChangesAsync();
    // Sem Teams aqui: o canal recebe só percurso completo do supervisionado.
    return Results.NoContent();
})
   .WithName("AddSupervisionado")
   .RequireAuthorization("Gestor");

// Remove a supervisão (só se a pessoa for supervisionada deste gestor).
app.MapDelete("/gestor/supervisionados/{usuarioId:guid}", async (Guid usuarioId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var usuario = await db.Usuarios.FindAsync(usuarioId);
    if (usuario is not null && usuario.GestorId == gestorId)
    {
        usuario.GestorId = null;
        await db.SaveChangesAsync();
    }

    return Results.NoContent();
})
   .WithName("RemoveSupervisionado")
   .RequireAuthorization("Gestor");

// --- Admin: CRUD de fases, passos, módulos e fluxos (reaproveita a policy "Gestor" como admin —
// sem 3º papel por ora). Torna a Jornada e o Guia editáveis pela tela em vez de fixos no seeder. ---

app.MapGet("/admin/fases", async (AppDbContext db) =>
    await db.Fases.OrderBy(f => f.Order).ToListAsync())
   .WithName("AdminGetFases")
   .RequireAuthorization("Gestor");

app.MapPost("/admin/fases", async (FaseRequest req, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Nome)) return Results.BadRequest(new { erro = "Informe o nome da fase." });

    var fase = new Fase { Nome = req.Nome.Trim(), Order = req.Order };
    db.Fases.Add(fase);
    await db.SaveChangesAsync();
    return Results.Ok(fase);
})
   .WithName("AdminCreateFase")
   .RequireAuthorization("Gestor");

app.MapPut("/admin/fases/{id:guid}", async (Guid id, FaseRequest req, AppDbContext db) =>
{
    var fase = await db.Fases.FindAsync(id);
    if (fase is null) return Results.NotFound(new { erro = "Fase não encontrada." });
    if (string.IsNullOrWhiteSpace(req.Nome)) return Results.BadRequest(new { erro = "Informe o nome da fase." });

    fase.Nome = req.Nome.Trim();
    fase.Order = req.Order;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("AdminUpdateFase")
   .RequireAuthorization("Gestor");

app.MapDelete("/admin/fases/{id:guid}", async (Guid id, AppDbContext db) =>
{
    if (await db.OnboardingSteps.AnyAsync(s => s.FaseId == id))
    {
        return Results.BadRequest(new { erro = "Essa fase tem passos vinculados — mova ou apague os passos primeiro." });
    }

    var fase = await db.Fases.FindAsync(id);
    if (fase is not null)
    {
        db.Fases.Remove(fase);
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
})
   .WithName("AdminDeleteFase")
   .RequireAuthorization("Gestor");

app.MapGet("/admin/modulos", async (AppDbContext db) =>
    await db.Modulos.OrderBy(m => m.Order).ToListAsync())
   .WithName("AdminGetModulos")
   .RequireAuthorization("Gestor");

app.MapPost("/admin/modulos", async (ModuloRequest req, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Nome)) return Results.BadRequest(new { erro = "Informe o nome do módulo." });

    var modulo = new Modulo { Nome = req.Nome.Trim(), Order = req.Order };
    db.Modulos.Add(modulo);
    await db.SaveChangesAsync();
    return Results.Ok(modulo);
})
   .WithName("AdminCreateModulo")
   .RequireAuthorization("Gestor");

app.MapPut("/admin/modulos/{id:guid}", async (Guid id, ModuloRequest req, AppDbContext db) =>
{
    var modulo = await db.Modulos.FindAsync(id);
    if (modulo is null) return Results.NotFound(new { erro = "Módulo não encontrado." });
    if (string.IsNullOrWhiteSpace(req.Nome)) return Results.BadRequest(new { erro = "Informe o nome do módulo." });

    modulo.Nome = req.Nome.Trim();
    modulo.Order = req.Order;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("AdminUpdateModulo")
   .RequireAuthorization("Gestor");

app.MapDelete("/admin/modulos/{id:guid}", async (Guid id, AppDbContext db) =>
{
    if (await db.Fluxos.AnyAsync(f => f.ModuloId == id))
    {
        return Results.BadRequest(new { erro = "Esse módulo tem fluxos vinculados — mova ou apague os fluxos primeiro." });
    }

    var modulo = await db.Modulos.FindAsync(id);
    if (modulo is not null)
    {
        db.Modulos.Remove(modulo);
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
})
   .WithName("AdminDeleteModulo")
   .RequireAuthorization("Gestor");

// Lista todo mundo (não só os supervisionados de quem chama) — a tela "Usuários" do admin usa isso
// pra decidir quem promover/demover.
app.MapGet("/admin/usuarios", async (AppDbContext db) =>
    await db.Usuarios
        .OrderBy(u => u.Nome)
        .Select(u => new
        {
            u.Id,
            u.Nome,
            Email = u.Email.Value,
            u.Cargo,
            u.Squad,
            u.IsGestor,
            u.Ativo,
            u.GestorId,
        })
        .ToListAsync())
   .WithName("AdminGetUsuarios")
   .RequireAuthorization("Gestor");

// Promove/demove um usuário a gestor. Sempre uma ação explícita de outro gestor (nunca a própria
// pessoa) — e nunca demove quem ainda tem supervisionados vinculados (mesmo padrão de guarda que
// Fase/Módulo já usam: primeiro desvincula, depois demove). Também não promove quem está com o
// acesso revogado (não faz sentido dar papel de gestor pra quem nem consegue entrar).
app.MapPut("/admin/usuarios/{id:guid}/gestor", async (Guid id, PromoverUsuarioRequest req, ClaimsPrincipal caller, AppDbContext db) =>
{
    if (!Guid.TryParse(caller.FindFirstValue("sub"), out var callerId) || callerId == id)
    {
        return Results.BadRequest(new { erro = "Você não pode mudar seu próprio papel de gestor." });
    }

    var usuario = await db.Usuarios.FindAsync(id);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });

    if (req.IsGestor && !usuario.Ativo)
    {
        return Results.BadRequest(new { erro = "Não dá pra tornar supervisor alguém com o acesso revogado." });
    }

    if (!req.IsGestor && await db.Usuarios.AnyAsync(u => u.GestorId == id))
    {
        return Results.BadRequest(new { erro = "Esse usuário ainda tem supervisionados vinculados — remova-os primeiro." });
    }

    usuario.IsGestor = req.IsGestor;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("AdminPromoverUsuario")
   .RequireAuthorization("Gestor");

// Revoga/reativa o acesso de um usuário (ex.: funcionário desligado). Mesma regra de guarda do
// papel de gestor: nunca a própria pessoa se revoga. Quando o alvo já tem um gestor vinculado, só
// ESSE gestor pode mexer no acesso dele (pedido explícito — antes qualquer gestor podia revogar
// supervisionado de outro); sem gestor vinculado, qualquer gestor pode (caso de off-boarding geral,
// sem dono específico ainda). Revogar NÃO desvincula `GestorId` — só esconde da lista de
// supervisionados (ver GET /gestor/usuarios); reativar traz de volta pro mesmo gestor sozinho.
app.MapPut("/admin/usuarios/{id:guid}/ativo", async (Guid id, AtivarUsuarioRequest req, ClaimsPrincipal caller, AppDbContext db) =>
{
    if (!Guid.TryParse(caller.FindFirstValue("sub"), out var callerId) || callerId == id)
    {
        return Results.BadRequest(new { erro = "Você não pode revogar o próprio acesso." });
    }

    var usuario = await db.Usuarios.FindAsync(id);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });

    if (usuario.GestorId.HasValue && usuario.GestorId.Value != callerId)
    {
        return Results.BadRequest(new { erro = "Só o gestor desse supervisionado pode mexer no acesso dele." });
    }

    usuario.Ativo = req.Ativo;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("AdminAtivarUsuario")
   .RequireAuthorization("Gestor");

// Lista/cadastra/remove e-mails pré-autorizados a virar gestor no cadastro (ver /auth/register).
app.MapGet("/admin/emails-autorizados", async (AppDbContext db) =>
    await db.EmailsAutorizadosGestor.OrderBy(e => e.Email).ToListAsync())
   .WithName("AdminGetEmailsAutorizados")
   .RequireAuthorization("Gestor");

app.MapPost("/admin/emails-autorizados", async (EmailAutorizadoRequest req, AppDbContext db, IConfiguration config) =>
{
    if (!Email.TryCreate(req.Email, out var email))
    {
        return Results.BadRequest(new { erro = "Email inválido." });
    }

    // Mesma trava de domínio do cadastro/login (Auth:DominioPermitido) — não faz sentido
    // pré-autorizar um e-mail que nem vai conseguir se cadastrar de verdade.
    var dominioPermitido = config["Auth:DominioPermitido"];
    if (!string.IsNullOrWhiteSpace(dominioPermitido)
        && !email!.Value.EndsWith($"@{dominioPermitido}", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { erro = $"Apenas e-mails @{dominioPermitido} podem ser pré-autorizados." });
    }

    if (await db.EmailsAutorizadosGestor.AnyAsync(e => e.Email == email!.Value))
    {
        return Results.BadRequest(new { erro = "Esse e-mail já está na lista." });
    }

    var autorizado = new EmailAutorizadoGestor { Email = email!.Value };
    db.EmailsAutorizadosGestor.Add(autorizado);
    await db.SaveChangesAsync();
    return Results.Ok(autorizado);
})
   .WithName("AdminCreateEmailAutorizado")
   .RequireAuthorization("Gestor");

app.MapDelete("/admin/emails-autorizados/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var autorizado = await db.EmailsAutorizadosGestor.FindAsync(id);
    if (autorizado is not null)
    {
        db.EmailsAutorizadosGestor.Remove(autorizado);
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
})
   .WithName("AdminDeleteEmailAutorizado")
   .RequireAuthorization("Gestor");

// Lista os passos com FaseId explícito (a colaborador-facing /onboarding/steps continua igual,
// pensada pra exibição, não edição).
// --- Admin: CRUD de Acessos (ex.: "E-mail Agilean", "Teams") — substitui a lista ilustrativa
// ACESSOS_POR_CARGO que era hardcoded no front. `CargoMinimo` é cumulativo: quem tem esse cargo ou
// um acima também precisa desse acesso (ver comentário na entidade `Acesso`). ---

app.MapGet("/admin/acessos", async (AppDbContext db) =>
    await db.Acessos.OrderBy(a => a.Order).ToListAsync())
   .WithName("AdminGetAcessos")
   .RequireAuthorization("Gestor");

app.MapPost("/admin/acessos", async (AcessoRequest req, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Nome)) return Results.BadRequest(new { erro = "Informe o nome do acesso." });

    var acesso = new Acesso
    {
        Nome = req.Nome.Trim(),
        Link = req.Link?.Trim() ?? string.Empty,
        CargoMinimo = req.CargoMinimo,
        Order = req.Order,
    };
    db.Acessos.Add(acesso);
    await db.SaveChangesAsync();
    return Results.Ok(acesso);
})
   .WithName("AdminCreateAcesso")
   .RequireAuthorization("Gestor");

app.MapPut("/admin/acessos/{id:guid}", async (Guid id, AcessoRequest req, AppDbContext db) =>
{
    var acesso = await db.Acessos.FindAsync(id);
    if (acesso is null) return Results.NotFound(new { erro = "Acesso não encontrado." });
    if (string.IsNullOrWhiteSpace(req.Nome)) return Results.BadRequest(new { erro = "Informe o nome do acesso." });

    acesso.Nome = req.Nome.Trim();
    acesso.Link = req.Link?.Trim() ?? string.Empty;
    acesso.CargoMinimo = req.CargoMinimo;
    acesso.Order = req.Order;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("AdminUpdateAcesso")
   .RequireAuthorization("Gestor");

app.MapDelete("/admin/acessos/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var acesso = await db.Acessos.FindAsync(id);
    if (acesso is not null)
    {
        db.Acessos.Remove(acesso);
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
})
   .WithName("AdminDeleteAcesso")
   .RequireAuthorization("Gestor");

app.MapGet("/admin/passos", async (AppDbContext db) =>
    await db.OnboardingSteps.OrderBy(s => s.Order).Select(s => new
    {
        s.Id,
        s.Order,
        s.FaseId,
        s.Title,
        s.Description,
        s.IsCompanySpecific,
        s.SkillArea,
        s.Conteudo,
        s.VideoUrl,
    }).ToListAsync())
   .WithName("AdminGetPassos")
   .RequireAuthorization("Gestor");

app.MapPost("/admin/passos", async (PassoRequest req, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { erro = "Informe o título do passo." });
    if (!await db.Fases.AnyAsync(f => f.Id == req.FaseId)) return Results.BadRequest(new { erro = "Fase inválida." });

    var passo = new OnboardingStep
    {
        FaseId = req.FaseId,
        Order = req.Order,
        Title = req.Title.Trim(),
        Description = req.Description,
        IsCompanySpecific = req.IsCompanySpecific,
        SkillArea = req.SkillArea,
        Conteudo = req.Conteudo,
        VideoUrl = req.VideoUrl,
    };
    db.OnboardingSteps.Add(passo);
    await db.SaveChangesAsync();
    return Results.Ok(passo);
})
   .WithName("AdminCreatePasso")
   .RequireAuthorization("Gestor");

app.MapPut("/admin/passos/{id:guid}", async (Guid id, PassoRequest req, AppDbContext db) =>
{
    var passo = await db.OnboardingSteps.FindAsync(id);
    if (passo is null) return Results.NotFound(new { erro = "Passo não encontrado." });
    if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { erro = "Informe o título do passo." });
    if (!await db.Fases.AnyAsync(f => f.Id == req.FaseId)) return Results.BadRequest(new { erro = "Fase inválida." });

    passo.FaseId = req.FaseId;
    passo.Order = req.Order;
    passo.Title = req.Title.Trim();
    passo.Description = req.Description;
    passo.IsCompanySpecific = req.IsCompanySpecific;
    passo.SkillArea = req.SkillArea;
    passo.Conteudo = req.Conteudo;
    passo.VideoUrl = req.VideoUrl;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("AdminUpdatePasso")
   .RequireAuthorization("Gestor");

app.MapDelete("/admin/passos/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var passo = await db.OnboardingSteps.FindAsync(id);
    if (passo is not null)
    {
        db.OnboardingSteps.Remove(passo);
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
})
   .WithName("AdminDeletePasso")
   .RequireAuthorization("Gestor");

// Lista os fluxos com ModuloId explícito (o /fluxos colaborador-facing continua igual).
app.MapGet("/admin/fluxos", async (AppDbContext db) =>
    await db.Fluxos.OrderBy(f => f.Modulo.Order).ThenBy(f => f.Order).Select(f => new
    {
        f.Id,
        f.Order,
        f.ModuloId,
        f.Squad,
        f.Categoria,
        f.Titulo,
        f.Descricao,
        f.Conteudo,
        f.VideoUrl,
    }).ToListAsync())
   .WithName("AdminGetFluxos")
   .RequireAuthorization("Gestor");

app.MapPost("/admin/fluxos", async (FluxoRequest req, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Titulo)) return Results.BadRequest(new { erro = "Informe o título do fluxo." });
    if (!await db.Modulos.AnyAsync(m => m.Id == req.ModuloId)) return Results.BadRequest(new { erro = "Módulo inválido." });

    var fluxo = new Fluxo
    {
        ModuloId = req.ModuloId,
        Squad = req.Squad,
        Categoria = req.Categoria,
        Order = req.Order,
        Titulo = req.Titulo.Trim(),
        Descricao = req.Descricao,
        Conteudo = req.Conteudo,
        VideoUrl = req.VideoUrl,
    };
    db.Fluxos.Add(fluxo);
    await db.SaveChangesAsync();
    return Results.Ok(fluxo);
})
   .WithName("AdminCreateFluxo")
   .RequireAuthorization("Gestor");

app.MapPut("/admin/fluxos/{id:guid}", async (Guid id, FluxoRequest req, AppDbContext db) =>
{
    var fluxo = await db.Fluxos.FindAsync(id);
    if (fluxo is null) return Results.NotFound(new { erro = "Fluxo não encontrado." });
    if (string.IsNullOrWhiteSpace(req.Titulo)) return Results.BadRequest(new { erro = "Informe o título do fluxo." });
    if (!await db.Modulos.AnyAsync(m => m.Id == req.ModuloId)) return Results.BadRequest(new { erro = "Módulo inválido." });

    fluxo.ModuloId = req.ModuloId;
    fluxo.Squad = req.Squad;
    fluxo.Categoria = req.Categoria;
    fluxo.Order = req.Order;
    fluxo.Titulo = req.Titulo.Trim();
    fluxo.Descricao = req.Descricao;
    fluxo.Conteudo = req.Conteudo;
    fluxo.VideoUrl = req.VideoUrl;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("AdminUpdateFluxo")
   .RequireAuthorization("Gestor");

app.MapDelete("/admin/fluxos/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var fluxo = await db.Fluxos.FindAsync(id);
    if (fluxo is not null)
    {
        db.Fluxos.Remove(fluxo);
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
})
   .WithName("AdminDeleteFluxo")
   .RequireAuthorization("Gestor");

// --- Notificações (do usuário logado, lido do token) ---

app.MapGet("/notificacoes", async (ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var itens = await db.Notificacoes
        .Where(n => n.UsuarioId == userId)
        .OrderByDescending(n => n.CriadaEm)
        .Take(30)
        .ToListAsync();

    // Resolve o autor (nome + foto) das notificações que têm, em lote, pra mostrar o avatar no sino.
    var autorIds = itens.Where(n => n.AutorId != null).Select(n => n.AutorId!.Value).Distinct().ToList();
    var autores = await db.Usuarios
        .Where(u => autorIds.Contains(u.Id))
        .ToDictionaryAsync(u => u.Id, u => new { u.Nome, u.Foto });

    var resultado = itens.Select(n =>
    {
        autores.TryGetValue(n.AutorId ?? Guid.Empty, out var autor);
        return new
        {
            n.Id,
            n.UsuarioId,
            n.Mensagem,
            n.Link,
            n.Lida,
            n.CriadaEm,
            n.AutorId,
            AutorNome = autor?.Nome,
            AutorFoto = autor?.Foto,
        };
    });

    return Results.Ok(resultado);
})
   .WithName("GetNotificacoes")
   .RequireAuthorization();

// Marca todas as não-lidas do usuário como lidas.
app.MapPost("/notificacoes/ler", async (ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var naoLidas = await db.Notificacoes
        .Where(n => n.UsuarioId == userId && !n.Lida)
        .ToListAsync();
    foreach (var n in naoLidas)
    {
        n.Lida = true;
    }
    if (naoLidas.Count > 0)
    {
        await db.SaveChangesAsync();
    }

    return Results.NoContent();
})
   .WithName("LerNotificacoes")
   .RequireAuthorization();

// Apaga uma notificação do usuário logado (silencioso se não existir ou for de outra pessoa —
// mesmo padrão de delete idempotente do resto do app).
app.MapDelete("/notificacoes/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var notificacao = await db.Notificacoes
        .FirstOrDefaultAsync(n => n.Id == id && n.UsuarioId == userId);
    if (notificacao is not null)
    {
        db.Notificacoes.Remove(notificacao);
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
})
   .WithName("ApagarNotificacao")
   .RequireAuthorization();

// Apaga todas as notificações do usuário logado (o botão "Limpar tudo" do sino).
app.MapDelete("/notificacoes", async (ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    await db.Notificacoes.Where(n => n.UsuarioId == userId).ExecuteDeleteAsync();
    return Results.NoContent();
})
   .WithName("ApagarTodasNotificacoes")
   .RequireAuthorization();

// --- Auth + Usuário + Progresso ---

// Login demo: get-or-create por email + emite JWT (token com expiração).
// Cadastro (auto-serviço): nome + email + senha → cria a conta e já loga.
app.MapPost("/auth/register", async (
    RegisterRequest req, AppDbContext db, TokenService tokens, IConfiguration config, EmailSender emailSender) =>
{
    if (string.IsNullOrWhiteSpace(req.Nome))
    {
        return Results.BadRequest(new { erro = "Informe seu nome." });
    }
    if (!Email.TryCreate(req.Email, out var email))
    {
        return Results.BadRequest(new { erro = "Email inválido." });
    }

    // Cadastro é restrito ao domínio da empresa — pedido do gestor (2026-08-13): evita acesso de
    // e-mails aleatórios. Vazio no appsettings desliga a checagem (dev sem essa config).
    var dominioPermitido = config["Auth:DominioPermitido"];
    if (!string.IsNullOrWhiteSpace(dominioPermitido)
        && !email!.Value.EndsWith($"@{dominioPermitido}", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { erro = $"Cadastro disponível apenas para e-mails @{dominioPermitido}." });
    }

    if (string.IsNullOrWhiteSpace(req.Senha) || req.Senha.Length < 6)
    {
        return Results.BadRequest(new { erro = "A senha precisa de ao menos 6 caracteres." });
    }
    if (await db.Usuarios.AnyAsync(u => u.Email == email))
    {
        return Results.BadRequest(new { erro = "Já existe uma conta com esse e-mail." });
    }

    var gestores = config.GetSection("Gestores").Get<string[]>() ?? [];
    var ehGestorPorConfig = gestores.Any(g => string.Equals(g, email!.Value, StringComparison.OrdinalIgnoreCase));
    var ehGestorPorLista = await db.EmailsAutorizadosGestor
        .AnyAsync(e => e.Email == email!.Value);
    // Cadastro por senha começa SEM confirmar (o domínio @agilean.com.br garante o formato, não
    // que a caixa existe de verdade) — só libera login depois do código de 6 dígitos mandado por
    // e-mail (ver EmailSender/`/auth/confirmar-email`). Login via Microsoft já nasce confirmado
    // (default da entidade), não passa por aqui.
    var usuario = new Usuario
    {
        Nome = req.Nome.Trim(),
        Email = email!,
        SenhaHash = SenhaHasher.Hash(req.Senha),
        IsGestor = ehGestorPorConfig || ehGestorPorLista,
        EmailConfirmado = false,
        CodigoConfirmacaoEmail = CodigoConfirmacao.Gerar(),
        CodigoConfirmacaoExpiraEm = DateTime.UtcNow.AddMinutes(CodigoConfirmacao.ValidoPorMinutos),
    };
    db.Usuarios.Add(usuario);
    await db.SaveChangesAsync();

    await emailSender.EnviarAsync(
        usuario.Email.Value,
        "Confirme seu e-mail — Bússola",
        $"Olá, {usuario.Nome}!\n\n"
            + $"Seu código de confirmação é: {usuario.CodigoConfirmacaoEmail}\n\n"
            + $"Ele expira em {CodigoConfirmacao.ValidoPorMinutos} minutos.");

    return Results.Ok(new { precisaConfirmarEmail = true, email = usuario.Email.Value });
})
   .WithName("Register");

// Confirma o código de 6 dígitos mandado no cadastro — só depois disso o login por senha libera
// (ver gate em `/auth/login`). Emite o token na hora (mesmo efeito de um login bem-sucedido).
app.MapPost("/auth/confirmar-email", async (ConfirmarEmailRequest req, AppDbContext db, TokenService tokens) =>
{
    if (!Email.TryCreate(req.Email, out var email))
    {
        return Results.BadRequest(new { erro = "Email inválido." });
    }

    var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Email == email);
    if (usuario is null || usuario.EmailConfirmado
        || usuario.CodigoConfirmacaoEmail != req.Codigo.Trim()
        || usuario.CodigoConfirmacaoExpiraEm is null
        || usuario.CodigoConfirmacaoExpiraEm < DateTime.UtcNow)
    {
        return Results.BadRequest(new { erro = "Código inválido ou expirado." });
    }

    usuario.EmailConfirmado = true;
    usuario.CodigoConfirmacaoEmail = null;
    usuario.CodigoConfirmacaoExpiraEm = null;
    await db.SaveChangesAsync();

    var (token, expiraEm) = tokens.Emitir(usuario);
    return Results.Ok(new
    {
        token,
        expiraEm,
        usuario = new { usuario.Id, usuario.Nome, Email = usuario.Email.Value, usuario.Cargo, usuario.Squad, usuario.IsGestor, usuario.Foto },
    });
})
   .WithName("ConfirmarEmail");

// Manda um código novo (substitui o anterior) — usado quando o e-mail não chega a tempo/expira.
// Resposta genérica sempre (não vaza se o e-mail tem conta ou já está confirmado).
app.MapPost("/auth/reenviar-codigo", async (ReenviarCodigoRequest req, AppDbContext db, EmailSender emailSender) =>
{
    if (Email.TryCreate(req.Email, out var email))
    {
        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Email == email);
        if (usuario is not null && !usuario.EmailConfirmado)
        {
            usuario.CodigoConfirmacaoEmail = CodigoConfirmacao.Gerar();
            usuario.CodigoConfirmacaoExpiraEm = DateTime.UtcNow.AddMinutes(CodigoConfirmacao.ValidoPorMinutos);
            await db.SaveChangesAsync();
            await emailSender.EnviarAsync(
                usuario.Email.Value,
                "Seu novo código — Bússola",
                $"Seu novo código de confirmação é: {usuario.CodigoConfirmacaoEmail}\n\n"
                    + $"Ele expira em {CodigoConfirmacao.ValidoPorMinutos} minutos.");
        }
    }
    return Results.Ok(new { ok = true });
})
   .WithName("ReenviarCodigoConfirmacao");

// Login: verifica e-mail + senha.
app.MapPost("/auth/login", async (LoginRequest req, AppDbContext db, TokenService tokens, IConfiguration config) =>
{
    if (!Email.TryCreate(req.Email, out var email))
    {
        return Results.BadRequest(new { erro = "Email inválido." });
    }

    var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Email == email);
    // Mesma mensagem genérica pra senha errada e conta desativada — não vaza que a conta existe
    // mas foi revogada (ex.: funcionário desligado).
    if (usuario is null || !usuario.Ativo || !SenhaHasher.Verificar(req.Senha, usuario.SenhaHash))
    {
        return Results.Json(new { erro = "E-mail ou senha inválidos." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    // Senha certa mas e-mail ainda não confirmado — a pessoa já provou que é dona da conta (acertou
    // a senha), então dá pra ser específico aqui sem vazar nada que ela não soubesse. `flag` própria
    // (não é só o texto do erro) pro front decidir levar direto pra tela de código.
    if (!usuario.EmailConfirmado)
    {
        return Results.Json(
            new { erro = "Confirme seu e-mail antes de entrar.", precisaConfirmarEmail = true },
            statusCode: StatusCodes.Status403Forbidden);
    }

    // Concede o papel de gestor se o e-mail está na lista do appsettings (config só ADICIONA o
    // papel, nunca remove — demover é sempre uma ação explícita de um gestor, nunca automática no
    // login; senão uma promoção manual feita pelo sistema seria desfeita no próximo login).
    var gestores = config.GetSection("Gestores").Get<string[]>() ?? [];
    var ehGestorPorConfig = gestores.Any(g => string.Equals(g, email!.Value, StringComparison.OrdinalIgnoreCase));
    if (ehGestorPorConfig && !usuario.IsGestor)
    {
        usuario.IsGestor = true;
        await db.SaveChangesAsync();
    }

    var (token, expiraEm) = tokens.Emitir(usuario);
    return Results.Ok(new
    {
        token,
        expiraEm,
        usuario = new { usuario.Id, usuario.Nome, Email = usuario.Email.Value, usuario.Cargo, usuario.Squad, usuario.IsGestor, usuario.Foto },
    });
})
   .WithName("Login");

// Login via Microsoft (Entra ID/Microsoft 365, o workspace da Agilean). O front autentica com
// MSAL.js e manda aqui o ACCESS TOKEN (escopo Graph "User.Read"). Em vez de validar o JWT
// localmente (issuer/JWKS de app multi-tenant é complexidade desnecessária pra esse porte), a
// validação é DELEGADA ao próprio Graph: se o token for real e válido, o Graph responde com o
// perfil; se não for, dá 401 — não precisamos confiar em mais nada além disso.
app.MapPost("/auth/microsoft", async (
    MicrosoftLoginRequest req, AppDbContext db, TokenService tokens, IConfiguration config,
    IHttpClientFactory httpClientFactory) =>
{
    var http = httpClientFactory.CreateClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", req.AccessToken);
    var respostaGraph = await http.GetAsync("https://graph.microsoft.com/v1.0/me");
    if (!respostaGraph.IsSuccessStatusCode)
    {
        return Results.Json(
            new { erro = "Não foi possível validar o login com a Microsoft." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var perfilGraph = await respostaGraph.Content.ReadFromJsonAsync<MicrosoftGraphMe>(
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    // `mail` fica em branco em algumas contas — `userPrincipalName` é o identificador de login
    // de verdade da conta corporativa e normalmente tem o mesmo formato de e-mail.
    var emailBruto = perfilGraph?.Mail ?? perfilGraph?.UserPrincipalName;
    if (string.IsNullOrWhiteSpace(emailBruto) || !Email.TryCreate(emailBruto, out var email))
    {
        return Results.BadRequest(new { erro = "Sua conta da Microsoft não retornou um e-mail válido." });
    }

    var dominioPermitido = config["Auth:DominioPermitido"];
    if (!string.IsNullOrWhiteSpace(dominioPermitido)
        && !email!.Value.EndsWith($"@{dominioPermitido}", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { erro = $"Login disponível apenas para e-mails @{dominioPermitido}." });
    }

    var gestoresCfg = config.GetSection("Gestores").Get<string[]>() ?? [];
    var ehGestorPorConfig = gestoresCfg.Any(g => string.Equals(g, email!.Value, StringComparison.OrdinalIgnoreCase));

    var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Email == email);
    if (usuario is null)
    {
        var ehGestorPorLista = await db.EmailsAutorizadosGestor.AnyAsync(e => e.Email == email!.Value);
        usuario = new Usuario
        {
            Nome = perfilGraph?.DisplayName?.Trim() is { Length: > 0 } nome ? nome : email!.Value,
            Email = email!,
            IsGestor = ehGestorPorConfig || ehGestorPorLista,
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();
    }
    else if (ehGestorPorConfig && !usuario.IsGestor)
    {
        // Mesma regra do login por senha: config só ADICIONA o papel, nunca remove.
        usuario.IsGestor = true;
        await db.SaveChangesAsync();
    }

    // Conta desativada (ex.: funcionário desligado) — recusa mesmo com um access token válido da
    // Microsoft, já que o desligamento pode não ter sido processado a tempo do lado do TI.
    if (!usuario.Ativo)
    {
        return Results.Json(
            new { erro = "Sua conta não tem mais acesso ao Bússola." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var (tokenBussola, expiraEmMicrosoft) = tokens.Emitir(usuario);
    return Results.Ok(new
    {
        token = tokenBussola,
        expiraEm = expiraEmMicrosoft,
        usuario = new { usuario.Id, usuario.Nome, Email = usuario.Email.Value, usuario.Cargo, usuario.Squad, usuario.IsGestor, usuario.Foto },
    });
})
   .WithName("LoginMicrosoft");

// --- Tokens de API (só gestor — pedido do Miguel 2026-09-10; autenticado por JWT normal, não dá
// pra gerenciar token só com outro token) ---

// Gera um token novo pro usuário logado — mesmo acesso dele, sem senha. Só devolve o valor em
// texto puro AQUI, na criação; dali pra frente só o hash fica guardado (ApiTokenHasher).
app.MapPost("/perfil/api-tokens", async (CriarApiTokenRequest req, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }
    if (string.IsNullOrWhiteSpace(req.Nome))
    {
        return Results.BadRequest(new { erro = "Dê um nome pro token (ex.: \"Claude Code\")." });
    }

    var valor = ApiTokenHasher.Gerar();
    var registro = new ApiToken { UsuarioId = userId, Nome = req.Nome.Trim(), TokenHash = ApiTokenHasher.Hash(valor) };
    db.ApiTokens.Add(registro);
    await db.SaveChangesAsync();

    return Results.Ok(new { registro.Id, registro.Nome, registro.CriadoEm, token = valor });
})
   .WithName("CriarApiToken")
   .RequireAuthorization("Gestor");

// Lista os tokens do usuário logado — nunca o valor em si, só nome/datas (pra ele saber o que
// existe e revogar o que não usa mais).
app.MapGet("/perfil/api-tokens", async (ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var tokens = await db.ApiTokens
        .Where(t => t.UsuarioId == userId)
        .OrderByDescending(t => t.CriadoEm)
        .Select(t => new { t.Id, t.Nome, t.CriadoEm, t.UltimoUsoEm })
        .ToListAsync();
    return Results.Ok(tokens);
})
   .WithName("ListarApiTokens")
   .RequireAuthorization("Gestor");

// Revoga (apaga) um token — só o próprio dono.
app.MapDelete("/perfil/api-tokens/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var registro = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.UsuarioId == userId);
    if (registro is not null)
    {
        db.ApiTokens.Remove(registro);
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
})
   .WithName("RevogarApiToken")
   .RequireAuthorization("Gestor");

// Salva o nivelamento (Perfil) no usuário.
app.MapPut("/users/{id:guid}/perfil", async (Guid id, SalvarPerfilRequest req, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId) || userId != id)
    {
        return Results.Forbid();
    }

    var usuario = await db.Usuarios.FindAsync(id);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });

    var perfil = req.Perfil;
    usuario.Cargo = perfil.Cargo;
    usuario.Frontend = perfil.Frontend;
    usuario.Backend = perfil.Backend;
    usuario.Git = perfil.Git;
    usuario.Sql = perfil.Sql;
    usuario.Jira = perfil.Jira;
    usuario.Squad = req.Squad;
    usuario.NivelamentoConcluido = true;
    await db.SaveChangesAsync();

    return Results.NoContent();
})
   .WithName("SalvarPerfil")
   .RequireAuthorization();

// Dados do usuário: perfil salvo + se já nivelou. O front usa no login pra decidir se pula o
// questionário e monta a trilha direto.
app.MapGet("/users/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId) || userId != id)
    {
        return Results.Forbid();
    }

    var usuario = await db.Usuarios.FindAsync(id);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });

    string? gestorNome = null;
    if (usuario.GestorId is Guid gestorId)
    {
        var gestor = await db.Usuarios.FindAsync(gestorId);
        gestorNome = gestor?.Nome;
    }

    return Results.Ok(new
    {
        usuario.Id,
        usuario.Nome,
        Email = usuario.Email.Value,
        usuario.Cargo,
        usuario.Squad,
        usuario.IsGestor,
        usuario.Foto,
        usuario.NivelamentoConcluido,
        GestorNome = gestorNome,
        perfil = usuario.ToPerfil(),
    });
})
   .WithName("GetUsuario")
   .RequireAuthorization();

// --- Perfil / Config (sempre do próprio usuário logado, via claim "sub") ---

// Troca o e-mail do usuário logado (valida formato + unicidade).
app.MapPut("/perfil/email", async (TrocarEmailRequest req, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }
    if (!Email.TryCreate(req.Email, out var email))
    {
        return Results.BadRequest(new { erro = "Email inválido." });
    }

    var usuario = await db.Usuarios.FindAsync(userId);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });

    if (email!.Value != usuario.Email.Value && await db.Usuarios.AnyAsync(u => u.Email == email))
    {
        return Results.BadRequest(new { erro = "Já existe uma conta com esse e-mail." });
    }

    usuario.Email = email!;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("TrocarEmail")
   .RequireAuthorization();

// Troca a senha do usuário logado (confere a atual + valida a nova).
app.MapPut("/perfil/senha", async (TrocarSenhaRequest req, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var usuario = await db.Usuarios.FindAsync(userId);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });

    if (!SenhaHasher.Verificar(req.SenhaAtual, usuario.SenhaHash))
    {
        return Results.BadRequest(new { erro = "Senha atual incorreta." });
    }
    if (string.IsNullOrWhiteSpace(req.NovaSenha) || req.NovaSenha.Length < 6)
    {
        return Results.BadRequest(new { erro = "A nova senha precisa de ao menos 6 caracteres." });
    }

    usuario.SenhaHash = SenhaHasher.Hash(req.NovaSenha);
    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("TrocarSenha")
   .RequireAuthorization();

// Define/remove a foto de perfil do usuário logado (data URI base64; vazio remove).
app.MapPut("/perfil/foto", async (TrocarFotoRequest req, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var usuario = await db.Usuarios.FindAsync(userId);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });

    var foto = req.Foto ?? string.Empty;
    // Guarda de tamanho — evita base64 gigante no banco (o front já reduz a imagem antes de enviar).
    if (foto.Length > 1_500_000)
    {
        return Results.BadRequest(new { erro = "Imagem muito grande. Use uma foto menor." });
    }

    usuario.Foto = foto;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("TrocarFoto")
   .RequireAuthorization();

// Lista os ids dos passos que o usuário já concluiu.
app.MapGet("/users/{id:guid}/progress", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId) || userId != id)
    {
        return Results.Forbid();
    }

    // `Completos` conta qualquer passo com registro (mesmo critério de sempre, é o que faz a
    // barra de progresso da fase/Jornada "sentir" o envio da comprovação como avanço — e sentir o
    // cancelamento como retrocesso, já que some o registro). `Pendentes` é o subconjunto ainda
    // aguardando o gestor (pediu correção OU aguardando primeira avaliação) — quem usa (front)
    // tira esses de dentro de `Completos` na hora de decidir se a FASE/Jornada fechou de verdade
    // (só conta 100% fechado depois da aprovação; a barra numérica já subiu antes disso).
    var registros = await db.PassosConcluidos.Where(passo => passo.UsuarioId == id).ToListAsync();
    var completos = registros.Select(p => p.OnboardingStepId).ToList();
    var pendentes = registros
        .Where(p => p.PrecisaCorrecao || p.AguardandoConfirmacao)
        .Select(p => p.OnboardingStepId)
        .ToList();
    return Results.Ok(new { Completos = completos, Pendentes = pendentes });
})
   .WithName("GetProgresso")
   .RequireAuthorization();

// Os PRÓPRIOS acessos do colaborador (leitura — quem marca é o gestor, na tela do
// Supervisionado). Sem isso, "acompanhe seus acessos" no modal de boas-vindas não tinha nenhuma
// tela de verdade por trás — o colaborador nunca via o próprio progresso de acesso.
app.MapGet("/users/{id:guid}/acessos", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId) || userId != id)
    {
        return Results.Forbid();
    }

    var usuario = await db.Usuarios.FindAsync(id);
    if (usuario is null) return Results.NotFound();

    var concluidos = (await db.AcessosConcluidos
        .Where(a => a.UsuarioId == id).Select(a => a.AcessoId).ToListAsync()).ToHashSet();

    var acessos = await db.Acessos
        .Where(a => a.CargoMinimo <= usuario.Cargo)
        .OrderBy(a => a.Order)
        .Select(a => new
        {
            a.Id,
            a.Nome,
            a.Link,
            Concluido = concluidos.Contains(a.Id),
        })
        .ToListAsync();

    return Results.Ok(acessos);
})
   .WithName("GetMeusAcessos")
   .RequireAuthorization();

// Marca um passo como concluído (idempotente).
app.MapPost("/users/{id:guid}/progress/{stepId:guid}", async (Guid id, Guid stepId, ConcluirPassoRequest? req, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId) || userId != id)
    {
        return Results.Forbid();
    }

    var evidencia = req?.Evidencia?.Trim() ?? string.Empty;
    var registro = await db.PassosConcluidos
        .FirstOrDefaultAsync(passo => passo.UsuarioId == id && passo.OnboardingStepId == stepId);

    if (registro is not null)
    {
        // Já concluído: só atualiza a comprovação (sem re-notificar a fase).
        if (registro.Evidencia != evidencia)
        {
            registro.Evidencia = evidencia;
            await db.SaveChangesAsync();
        }
    }
    else
    {
        // O passo que fecha a trilha inteira (o único com comprovação/PR, literalmente o de maior
        // Order do sistema — mesmo critério do front, `ultimoItemDaTrilha`) entra direto no ciclo
        // de revisão do gestor: aguardando a primeira avaliação dele, mesmo sem nunca ter pedido
        // correção nenhuma.
        var maiorOrder = await db.OnboardingSteps.MaxAsync(s => (int?)s.Order) ?? -1;
        var stepDoRegistro = await db.OnboardingSteps.FindAsync(stepId);
        var exigeAvaliacaoDoGestor = stepDoRegistro is not null && stepDoRegistro.Order == maiorOrder;

        db.PassosConcluidos.Add(new PassoConcluido
        {
            UsuarioId = id,
            OnboardingStepId = stepId,
            Evidencia = evidencia,
            AguardandoConfirmacao = exigeAvaliacaoDoGestor,
        });
        await db.SaveChangesAsync();

        var usuario = await db.Usuarios.FindAsync(id);
        if (usuario?.GestorId is Guid gestorId)
        {
            // Esse aviso é SEPARADO do de "fase concluída" abaixo — enviar a comprovação não fecha
            // mais a fase sozinho (só a aprovação do gestor fecha, ver AguardandoConfirmacao), mas
            // o gestor precisa saber NA HORA que tem um PR esperando ele, senão ninguém nunca
            // saberia que precisa entrar e avaliar. `?destaque=primeiro-card` faz a tela do
            // supervisionado abrir o dropdown certo e piscar pra ele ver onde é.
            if (exigeAvaliacaoDoGestor)
            {
                var msgEnviou = $"{usuario.Nome} enviou a comprovação do Primeiro Card — aguardando sua avaliação.";
                db.Notificacoes.Add(new Notificacao
                {
                    UsuarioId = gestorId,
                    Mensagem = msgEnviou,
                    AutorId = id,
                    Link = $"/supervisionado/{id}?destaque=primeiro-card",
                });
                await db.SaveChangesAsync();
                await teams.EnviarAsync(msgEnviou);
            }

            // Só avisa o gestor (sino sempre; Teams só na fase "Primeiro Card") quando a FASE
            // inteira do passo é concluída — se esse passo entrou aguardando avaliação do gestor
            // (`exigeAvaliacaoDoGestor` acima), a fase NÃO conta como completa ainda (a função
            // abaixo já filtra por `!PrecisaCorrecao && !AguardandoConfirmacao`); só dispara de
            // fato quando o gestor aprovar (ver ConfirmarCorrecaoPasso, que chama a mesma função).
            await NotificarSeFaseCompletaAsync(db, id, usuario.Nome, gestorId, stepId, evidencia);
        }
    }

    return Results.NoContent();
})
   .WithName("ConcluirPasso")
   .RequireAuthorization();

// Desmarca um passo (toggle).
app.MapDelete("/users/{id:guid}/progress/{stepId:guid}", async (Guid id, Guid stepId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId) || userId != id)
    {
        return Results.Forbid();
    }

    var passo = await db.PassosConcluidos
        .FirstOrDefaultAsync(p => p.UsuarioId == id && p.OnboardingStepId == stepId);

    if (passo is not null)
    {
        db.PassosConcluidos.Remove(passo);

        // Cancelar o envio do passo que exige avaliação do gestor (o único com esse ciclo de
        // revisão) invalida qualquer notificação que já tinha avisado ele sobre esse PR
        // (comprovação enviada, corrigido, fase concluída) — sem isso, ele clicaria numa
        // notificação velha e não acharia mais nada pra revisar (a comprovação já sumiu).
        var maiorOrder = await db.OnboardingSteps.MaxAsync(s => (int?)s.Order) ?? -1;
        var stepDoPasso = await db.OnboardingSteps.FindAsync(stepId);
        if (stepDoPasso is not null && stepDoPasso.Order == maiorOrder)
        {
            var notificacoesObsoletas = await db.Notificacoes
                .Where(n => n.AutorId == id && n.Link == $"/supervisionado/{id}?destaque=primeiro-card")
                .ToListAsync();
            db.Notificacoes.RemoveRange(notificacoesObsoletas);
        }

        await db.SaveChangesAsync();
    }

    return Results.NoContent();
})
   .WithName("DesmarcarPasso")
   .RequireAuthorization();

// Comprovação (evidência) de um passo — a tela do passo usa pra pré-preencher o que já foi anexado.
app.MapGet("/users/{id:guid}/progress/{stepId:guid}", async (Guid id, Guid stepId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId) || userId != id)
    {
        return Results.Forbid();
    }

    var registro = await db.PassosConcluidos
        .FirstOrDefaultAsync(p => p.UsuarioId == id && p.OnboardingStepId == stepId);
    return Results.Ok(new
    {
        Concluido = registro is not null,
        Evidencia = registro?.Evidencia ?? string.Empty,
        PrecisaCorrecao = registro?.PrecisaCorrecao ?? false,
        QtdCorrecoes = registro?.QtdCorrecoes ?? 0,
        AguardandoConfirmacao = registro?.AguardandoConfirmacao ?? false,
    });
})
   .WithName("GetComprovacaoPasso")
   .RequireAuthorization();

// Gestor pede correção no PR já enviado como comprovação (os comentários em si ficam no Bitbucket
// — isso aqui é só o status/aviso). Só o gestor liga esse flag; só o colaborador desliga (endpoint
// abaixo), depois de corrigir e atualizar a MESMA branch/PR.
app.MapPut("/gestor/usuarios/{usuarioId:guid}/passos/{stepId:guid}/pedir-correcao", async (
    Guid usuarioId, Guid stepId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var alvo = await db.Usuarios.FindAsync(usuarioId);
    if (alvo is null || alvo.GestorId != gestorId)
    {
        return Results.NotFound(new { erro = "Supervisionado não encontrado." });
    }

    var registro = await db.PassosConcluidos
        .FirstOrDefaultAsync(p => p.UsuarioId == usuarioId && p.OnboardingStepId == stepId);
    if (registro is null)
    {
        return Results.NotFound(new { erro = "Esse passo ainda não foi concluído/tem comprovação." });
    }
    var step = await db.OnboardingSteps.FindAsync(stepId);
    if (step is null)
    {
        return Results.NotFound(new { erro = "Passo não encontrado." });
    }

    registro.PrecisaCorrecao = true;
    registro.AguardandoConfirmacao = false;

    var gestorNome = user.FindFirstValue("nome") ?? "Seu gestor";
    db.Notificacoes.Add(new Notificacao
    {
        UsuarioId = usuarioId,
        Mensagem = $"{gestorNome} pediu uma correção no seu PR do Primeiro Card — confira os comentários no Bitbucket.",
        AutorId = gestorId,
        // A rota e por TITULO, nao por Id (ver hrefDoItem em JornadaView.tsx).
        Link = $"/passo/{Uri.EscapeDataString(step.Title)}",
    });

    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("PedirCorrecaoPasso")
   .RequireAuthorization("Gestor");

// Colaborador marca que já corrigiu (fez push na mesma branch/PR) — avisa o gestor que já pode
// conferir de novo. Só o colaborador (dono da comprovação) pode fazer essa transição.
app.MapPut("/users/{id:guid}/progress/{stepId:guid}/corrigido", async (
    Guid id, Guid stepId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId) || userId != id)
    {
        return Results.Forbid();
    }

    var registro = await db.PassosConcluidos
        .FirstOrDefaultAsync(p => p.UsuarioId == id && p.OnboardingStepId == stepId);
    if (registro is null)
    {
        return Results.NotFound(new { erro = "Esse passo ainda não foi concluído/tem comprovação." });
    }

    registro.PrecisaCorrecao = false;
    registro.QtdCorrecoes += 1;
    registro.AguardandoConfirmacao = true;

    var usuario = await db.Usuarios.FindAsync(id);
    if (usuario?.GestorId is Guid gestorId)
    {
        var msg = $"{usuario.Nome} marcou o PR do Primeiro Card como corrigido — confira e confirme.";
        db.Notificacoes.Add(new Notificacao
        {
            UsuarioId = gestorId,
            Mensagem = msg,
            AutorId = id,
            Link = $"/supervisionado/{id}?destaque=primeiro-card",
        });
        await db.SaveChangesAsync();

        // Mesmo critério de sempre: Teams é só pra avisar o GESTOR de marcos do Primeiro Card
        // (chegou na fase, concluiu, e agora corrigiu) — nunca notifica o colaborador por lá.
        await teams.EnviarAsync(msg);
        return Results.NoContent();
    }

    await db.SaveChangesAsync();
    return Results.NoContent();
})
   .WithName("MarcarCorrigido")
   .RequireAuthorization();

// Gestor confere a correção que o colaborador marcou e confirma que está tudo certo de verdade —
// fecha o ciclo (AguardandoConfirmacao = false) e avisa o colaborador. Se não estiver bom, o gestor
// usa o "pedir correção" de novo em vez desse endpoint.
app.MapPut("/gestor/usuarios/{usuarioId:guid}/passos/{stepId:guid}/confirmar", async (
    Guid usuarioId, Guid stepId, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var alvo = await db.Usuarios.FindAsync(usuarioId);
    if (alvo is null || alvo.GestorId != gestorId)
    {
        return Results.NotFound(new { erro = "Supervisionado não encontrado." });
    }

    var registro = await db.PassosConcluidos
        .FirstOrDefaultAsync(p => p.UsuarioId == usuarioId && p.OnboardingStepId == stepId);
    if (registro is null)
    {
        return Results.NotFound(new { erro = "Esse passo ainda não foi concluído/tem comprovação." });
    }
    var step = await db.OnboardingSteps.FindAsync(stepId);
    if (step is null)
    {
        return Results.NotFound(new { erro = "Passo não encontrado." });
    }
    // Idempotente: se já não tava aguardando (aprovado de novo por engano, duplo clique etc.), não
    // reenvia notificação nem reavalia a fase de novo.
    if (!registro.AguardandoConfirmacao)
    {
        return Results.NoContent();
    }

    registro.AguardandoConfirmacao = false;

    var gestorNome = user.FindFirstValue("nome") ?? "Seu gestor";
    // Só fala em "correção" se já teve pelo menos um ciclo de pedir-corrigir — na primeira
    // avaliação (nunca pediu correção nenhuma) isso soaria estranho, já que nada foi corrigido.
    var mensagemAprovacao = registro.QtdCorrecoes > 0
        ? $"{gestorNome} aprovou a correção do seu PR do Primeiro Card — tudo certo!"
        : $"{gestorNome} aprovou o seu PR do Primeiro Card — tudo certo!";
    db.Notificacoes.Add(new Notificacao
    {
        UsuarioId = usuarioId,
        Mensagem = mensagemAprovacao,
        AutorId = gestorId,
        // A rota e por TITULO, nao por Id (ver hrefDoItem em JornadaView.tsx).
        Link = $"/passo/{Uri.EscapeDataString(step.Title)}",
    });
    await db.SaveChangesAsync();

    // A aprovação pode ser exatamente o que faltava pra fechar a fase Primeiro Card (e a Jornada
    // inteira) — mesma checagem/notificação de quando um passo comum é concluído.
    await NotificarSeFaseCompletaAsync(db, usuarioId, alvo.Nome, gestorId, stepId, registro.Evidencia);

    return Results.NoContent();
})
   .WithName("ConfirmarCorrecaoPasso")
   .RequireAuthorization("Gestor");

// Leitura própria (colaborador) do link do card — usada pra saber se a fase Primeiro Card já
// libera os passos ou se ainda está esperando o gestor mandar (null = ainda esperando).
app.MapGet("/users/{id:guid}/card-link", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId) || userId != id)
    {
        return Results.Forbid();
    }

    var cardLink = await db.CardLinks.FirstOrDefaultAsync(c => c.UsuarioId == id);
    return Results.Ok(new { Url = cardLink?.Url });
})
   .WithName("GetMeuCardLink")
   .RequireAuthorization();

app.Run();

// Corpos de autenticação.
record LoginRequest(string Email, string Senha);
record RegisterRequest(string Nome, string Email, string Senha);
record MicrosoftLoginRequest(string AccessToken);
record ConfirmarEmailRequest(string Email, string Codigo);
record ReenviarCodigoRequest(string Email);
record CriarApiTokenRequest(string Nome);

// Só os campos que a gente usa da resposta do Microsoft Graph `GET /me`.
record MicrosoftGraphMe(string? Mail, string? UserPrincipalName, string? DisplayName);

// Corpos do perfil/config (do próprio usuário logado).
record TrocarEmailRequest(string Email);
record TrocarSenhaRequest(string SenhaAtual, string NovaSenha);
record TrocarFotoRequest(string? Foto);

// Corpo do salvar-perfil (nivelamento): perfil de skills + squad.
record SalvarPerfilRequest(Perfil Perfil, Squad Squad);

// Corpo do concluir-passo: comprovação opcional (link do PR, print ou nota).
record ConcluirPassoRequest(string? Evidencia);

// Um item da trilha. Unifica passo de onboarding e fluxo do squad no mesmo formato — `Tipo`
// ("passo" | "fluxo") diz ao front pra onde navegar e onde marcar a conclusão.
record TrailItemView(
    Guid Id,
    int Order,
    string Phase,
    string Title,
    string Description,
    bool IsCompanySpecific,
    SkillArea SkillArea,
    string Conteudo,
    StepDepth RecommendedDepth,
    string Tipo);

// Corpos do CRUD de admin (fases/passos/módulos/fluxos).
record FaseRequest(string Nome, int Order);
record ModuloRequest(string Nome, int Order);
record AcessoRequest(string Nome, string? Link, Cargo CargoMinimo, int Order);
record MarcarAcessoRequest(bool Concluido);
record CardLinkRequest(string? Url);
record PromoverUsuarioRequest(bool IsGestor);
record AtivarUsuarioRequest(bool Ativo);
record EmailAutorizadoRequest(string Email);
record PassoRequest(
    Guid FaseId,
    int Order,
    string Title,
    string Description,
    bool IsCompanySpecific,
    SkillArea SkillArea,
    string Conteudo,
    string VideoUrl);
record FluxoRequest(
    Guid ModuloId,
    Squad? Squad,
    string Categoria,
    int Order,
    string Titulo,
    string Descricao,
    string Conteudo,
    string VideoUrl);
