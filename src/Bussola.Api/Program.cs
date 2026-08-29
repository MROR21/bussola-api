using System.Security.Claims;
using System.Text;
using Bussola.Api.Auth;
using Bussola.Api.Services;
using Bussola.Domain.Entities;
using Bussola.Domain.Nivelamento;
using Bussola.Domain.ValueObjects;
using Bussola.Infrastructure.Data;
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

// Validação do JWT (Auth B): protege os endpoints do gestor. O front manda o Bearer token.
var jwtKey = builder.Configuration["Jwt:Key"]!;
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("Gestor", policy => policy.RequireClaim("gestor", "true")));

var app = builder.Build();

// TeamsNotifier é singleton → resolvo uma vez e uso nos eventos (evita injetar em cada endpoint).
var teams = app.Services.GetRequiredService<TeamsNotifier>();

// Ao iniciar: aplica migrations pendentes e semeia os dados iniciais (dev).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await OnboardingSeeder.SeedAsync(db);
    await FluxoSeeder.SeedAsync(db);

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
app.MapGet("/onboarding/steps", async (AppDbContext db) =>
    await db.OnboardingSteps
        .OrderBy(step => step.Order)
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

    var steps = await db.OnboardingSteps.Include(step => step.Fase).OrderBy(step => step.Order).ToListAsync();
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
        .OrderBy(fluxo => fluxo.Order)
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

        // Só avisa o gestor (Teams + sino) quando o MÓDULO inteiro (fluxos visíveis) fecha.
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
                    var msg = $"{usuario.Nome} concluiu o módulo {fluxo.Modulo.Nome}.";
                    db.Notificacoes.Add(new Notificacao { UsuarioId = gestorId, Mensagem = msg, AutorId = userId });
                    await db.SaveChangesAsync();
                    await teams.EnviarAsync(msg);
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
    var concluidosPorUsuario = await db.PassosConcluidos
        .GroupBy(passo => passo.UsuarioId)
        .Select(grupo => new { UsuarioId = grupo.Key, Total = grupo.Count() })
        .ToDictionaryAsync(x => x.UsuarioId, x => x.Total);

    var usuarios = await db.Usuarios
        .Where(u => u.GestorId == gestorId)
        .OrderBy(u => u.Nome)
        .ToListAsync();

    // Projeção em memória: Email é Value Object (não dá pra projetar .Value no SQL).
    var resultado = usuarios.Select(u => new
    {
        u.Id,
        u.Nome,
        Email = u.Email.Value,
        u.Cargo,
        u.Squad,
        u.IsGestor,
        u.NivelamentoConcluido,
        PassosConcluidos = concluidosPorUsuario.GetValueOrDefault(u.Id, 0),
        TotalPassos = totalPassos,
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

    var steps = await db.OnboardingSteps.Include(s => s.Fase).OrderBy(s => s.Order).ToListAsync();
    var passos = steps.Select(s => new
    {
        s.Id,
        s.Order,
        Phase = s.Fase.Nome,
        s.Title,
        Concluido = evidenciaPorStep.ContainsKey(s.Id),
        Evidencia = evidenciaPorStep.GetValueOrDefault(s.Id, string.Empty),
    });

    return Results.Ok(new { alvo.Nome, Passos = passos });
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

    var todos = await db.Fluxos.Include(f => f.Modulo).OrderBy(f => f.Order).ToListAsync();
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
        })
        .ToListAsync())
   .WithName("AdminGetUsuarios")
   .RequireAuthorization("Gestor");

// Promove/demove um usuário a gestor. Sempre uma ação explícita de outro gestor (nunca a própria
// pessoa) — e nunca demove quem ainda tem supervisionados vinculados (mesmo padrão de guarda que
// Fase/Módulo já usam: primeiro desvincula, depois demove).
app.MapPut("/admin/usuarios/{id:guid}/gestor", async (Guid id, PromoverUsuarioRequest req, ClaimsPrincipal caller, AppDbContext db) =>
{
    if (!Guid.TryParse(caller.FindFirstValue("sub"), out var callerId) || callerId == id)
    {
        return Results.BadRequest(new { erro = "Você não pode mudar seu próprio papel de gestor." });
    }

    var usuario = await db.Usuarios.FindAsync(id);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });

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

// Lista/cadastra/remove e-mails pré-autorizados a virar gestor no cadastro (ver /auth/register).
app.MapGet("/admin/emails-autorizados", async (AppDbContext db) =>
    await db.EmailsAutorizadosGestor.OrderBy(e => e.Email).ToListAsync())
   .WithName("AdminGetEmailsAutorizados")
   .RequireAuthorization("Gestor");

app.MapPost("/admin/emails-autorizados", async (EmailAutorizadoRequest req, AppDbContext db) =>
{
    if (!Email.TryCreate(req.Email, out var email))
    {
        return Results.BadRequest(new { erro = "Email inválido." });
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
    await db.Fluxos.OrderBy(f => f.Order).Select(f => new
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

// --- Auth + Usuário + Progresso ---

// Login demo: get-or-create por email + emite JWT (token com expiração).
// Cadastro (auto-serviço): nome + email + senha → cria a conta e já loga.
app.MapPost("/auth/register", async (RegisterRequest req, AppDbContext db, TokenService tokens, IConfiguration config) =>
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
    var usuario = new Usuario
    {
        Nome = req.Nome.Trim(),
        Email = email!,
        SenhaHash = SenhaHasher.Hash(req.Senha),
        IsGestor = ehGestorPorConfig || ehGestorPorLista,
    };
    db.Usuarios.Add(usuario);
    await db.SaveChangesAsync();

    var (token, expiraEm) = tokens.Emitir(usuario);
    return Results.Ok(new
    {
        token,
        expiraEm,
        usuario = new { usuario.Id, usuario.Nome, Email = usuario.Email.Value, usuario.Cargo, usuario.Squad, usuario.IsGestor, usuario.Foto },
    });
})
   .WithName("Register");

// Login: verifica e-mail + senha.
app.MapPost("/auth/login", async (LoginRequest req, AppDbContext db, TokenService tokens, IConfiguration config) =>
{
    if (!Email.TryCreate(req.Email, out var email))
    {
        return Results.BadRequest(new { erro = "Email inválido." });
    }

    var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Email == email);
    if (usuario is null || !SenhaHasher.Verificar(req.Senha, usuario.SenhaHash))
    {
        return Results.Json(new { erro = "E-mail ou senha inválidos." }, statusCode: StatusCodes.Status401Unauthorized);
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

    var concluidos = await db.PassosConcluidos
        .Where(passo => passo.UsuarioId == id)
        .Select(passo => passo.OnboardingStepId)
        .ToListAsync();
    return Results.Ok(concluidos);
})
   .WithName("GetProgresso")
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
        db.PassosConcluidos.Add(new PassoConcluido { UsuarioId = id, OnboardingStepId = stepId, Evidencia = evidencia });
        await db.SaveChangesAsync();

        // Só avisa o gestor (Teams + sino) quando a FASE inteira do passo é concluída.
        var usuario = await db.Usuarios.FindAsync(id);
        if (usuario?.GestorId is Guid gestorId)
        {
            var step = await db.OnboardingSteps.Include(s => s.Fase).FirstOrDefaultAsync(s => s.Id == stepId);
            if (step is not null)
            {
                var idsDaFase = await db.OnboardingSteps
                    .Where(s => s.FaseId == step.FaseId)
                    .Select(s => s.Id)
                    .ToListAsync();
                var concluidosDaFase = await db.PassosConcluidos
                    .CountAsync(p => p.UsuarioId == id && idsDaFase.Contains(p.OnboardingStepId));

                if (idsDaFase.Count > 0 && concluidosDaFase >= idsDaFase.Count)
                {
                    var msg = $"{usuario.Nome} concluiu a fase {step.Fase.Nome}.";
                    db.Notificacoes.Add(new Notificacao { UsuarioId = gestorId, Mensagem = msg, AutorId = id });
                    await db.SaveChangesAsync();
                    await teams.EnviarAsync(msg);
                }
            }
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
    });
})
   .WithName("GetComprovacaoPasso")
   .RequireAuthorization();

app.Run();

// Corpos de autenticação.
record LoginRequest(string Email, string Senha);
record RegisterRequest(string Nome, string Email, string Senha);

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
record PromoverUsuarioRequest(bool IsGestor);
record EmailAutorizadoRequest(string Email);
record PassoRequest(
    Guid FaseId,
    int Order,
    string Title,
    string Description,
    bool IsCompanySpecific,
    SkillArea SkillArea,
    string Conteudo);
record FluxoRequest(
    Guid ModuloId,
    Squad? Squad,
    string Categoria,
    int Order,
    string Titulo,
    string Descricao,
    string Conteudo,
    string VideoUrl);
