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

// Lista os passos de onboarding, ordenados. O AppDbContext é injetado pelo ASP.NET.
app.MapGet("/onboarding/steps", async (AppDbContext db) =>
    await db.OnboardingSteps
        .OrderBy(step => step.Order)
        .ToListAsync())
   .WithName("GetOnboardingSteps");

// Monta a trilha para um perfil: cada passo com a profundidade recomendada (essencial/resumo).
// Stateless — o perfil vem no corpo, nada é salvo (persistir vem quando existir Usuário).
app.MapPost("/onboarding/trail", async (Perfil perfil, AppDbContext db) =>
{
    var steps = await db.OnboardingSteps.OrderBy(step => step.Order).ToListAsync();

    var trail = steps.Select(step => new
    {
        step.Id,
        step.Order,
        step.Phase,
        step.Title,
        step.Description,
        step.IsCompanySpecific,
        step.SkillArea,
        step.Conteudo,
        RecommendedDepth = TrailPlanner.DepthFor(step, perfil),
    });

    return Results.Ok(trail);
})
   .WithName("GetOnboardingTrail");

// Um passo específico (com o conteúdo em Markdown). Usado na página de detalhe do passo.
app.MapGet("/onboarding/steps/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var step = await db.OnboardingSteps.FindAsync(id);
    return step is null
        ? Results.NotFound(new { erro = "Passo não encontrado." })
        : Results.Ok(step);
})
   .WithName("GetOnboardingStep");

// --- Fluxos (Referência viva) ---

// Lista todos os fluxos, ordenados. A busca é feita no front (poucos itens).
app.MapGet("/fluxos", async (AppDbContext db) =>
    await db.Fluxos.OrderBy(fluxo => fluxo.Order).ToListAsync())
   .WithName("GetFluxos");

// Um fluxo específico (com o conteúdo em Markdown).
app.MapGet("/fluxos/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var fluxo = await db.Fluxos.FindAsync(id);
    return fluxo is null
        ? Results.NotFound(new { erro = "Fluxo não encontrado." })
        : Results.Ok(fluxo);
})
   .WithName("GetFluxo");

// Fluxos visíveis do usuário logado: do squad dele + os sem squad (Básico) + os atribuídos pelo gestor.
app.MapGet("/fluxos/meus", async (ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var usuario = await db.Usuarios.FindAsync(userId);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });

    var atribuidos = (await db.FluxosAtribuidos
        .Where(a => a.UsuarioId == userId)
        .Select(a => a.FluxoId)
        .ToListAsync()).ToHashSet();

    var todos = await db.Fluxos.OrderBy(f => f.Order).ToListAsync();
    var visiveis = todos.Where(f => f.Squad == null || f.Squad == usuario.Squad || atribuidos.Contains(f.Id));

    return Results.Ok(visiveis);
})
   .WithName("GetMeusFluxos")
   .RequireAuthorization();

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
            var fluxo = await db.Fluxos.FindAsync(fluxoId);
            if (fluxo is not null)
            {
                // Fluxos visíveis do módulo pra este usuário (mesma regra do /fluxos/meus).
                var atribuidos = (await db.FluxosAtribuidos
                    .Where(a => a.UsuarioId == userId)
                    .Select(a => a.FluxoId)
                    .ToListAsync()).ToHashSet();
                var idsVisiveis = (await db.Fluxos.Where(f => f.Modulo == fluxo.Modulo).ToListAsync())
                    .Where(f => f.Squad == null || f.Squad == usuario.Squad || atribuidos.Contains(f.Id))
                    .Select(f => f.Id)
                    .ToList();
                var concluidosDoModulo = await db.FluxosConcluidos
                    .CountAsync(f => f.UsuarioId == userId && idsVisiveis.Contains(f.FluxoId));

                if (idsVisiveis.Count > 0 && concluidosDoModulo >= idsVisiveis.Count)
                {
                    var msg = $"{usuario.Nome} concluiu o módulo {fluxo.Modulo}.";
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

    var steps = await db.OnboardingSteps.OrderBy(s => s.Order).ToListAsync();
    var passos = steps.Select(s => new
    {
        s.Id,
        s.Order,
        s.Phase,
        s.Title,
        Concluido = evidenciaPorStep.ContainsKey(s.Id),
        Evidencia = evidenciaPorStep.GetValueOrDefault(s.Id, string.Empty),
    });

    return Results.Ok(new { alvo.Nome, Passos = passos });
})
   .WithName("GetProgressoSupervisionado")
   .RequireAuthorization("Gestor");

// Fluxos que o supervisionado vê (squad + Básico + atribuídos), com a flag de concluído.
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

    var atribuidos = (await db.FluxosAtribuidos
        .Where(a => a.UsuarioId == usuarioId).Select(a => a.FluxoId).ToListAsync()).ToHashSet();
    var concluidos = (await db.FluxosConcluidos
        .Where(f => f.UsuarioId == usuarioId).Select(f => f.FluxoId).ToListAsync()).ToHashSet();

    var todos = await db.Fluxos.OrderBy(f => f.Order).ToListAsync();
    var visiveis = todos
        .Where(f => f.Squad == null || f.Squad == alvo.Squad || atribuidos.Contains(f.Id))
        .Select(f => new { f.Id, f.Titulo, f.Modulo, Concluido = concluidos.Contains(f.Id) });

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

// Atribuições de fluxo dos supervisionados do gestor (qual supervisionado tem qual fluxo).
app.MapGet("/gestor/fluxos/atribuicoes", async (ClaimsPrincipal user, AppDbContext db) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var gestorId))
    {
        return Results.Unauthorized();
    }

    var supervisionados = await db.Usuarios.Where(u => u.GestorId == gestorId).ToListAsync();
    var nomePorId = supervisionados.ToDictionary(u => u.Id, u => u.Nome);
    var ids = nomePorId.Keys.ToList();

    var atribuicoes = await db.FluxosAtribuidos.Where(a => ids.Contains(a.UsuarioId)).ToListAsync();
    var resultado = atribuicoes.Select(a => new { a.FluxoId, a.UsuarioId, Nome = nomePorId[a.UsuarioId] });

    return Results.Ok(resultado);
})
   .WithName("GetAtribuicoes")
   .RequireAuthorization("Gestor");

// Atribui (libera) um fluxo a um supervisionado do gestor. Idempotente + notifica o supervisionado.
app.MapPost("/gestor/fluxos/{fluxoId:guid}/atribuir/{usuarioId:guid}", async (Guid fluxoId, Guid usuarioId, ClaimsPrincipal user, AppDbContext db) =>
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

    var fluxo = await db.Fluxos.FindAsync(fluxoId);
    if (fluxo is null) return Results.NotFound(new { erro = "Fluxo não encontrado." });

    var jaTem = await db.FluxosAtribuidos.AnyAsync(a => a.FluxoId == fluxoId && a.UsuarioId == usuarioId);
    if (!jaTem)
    {
        db.FluxosAtribuidos.Add(new FluxoAtribuido { FluxoId = fluxoId, UsuarioId = usuarioId });
        db.Notificacoes.Add(new Notificacao
        {
            UsuarioId = usuarioId,
            Mensagem = $"Seu gestor liberou o fluxo: {fluxo.Titulo}.",
            Link = $"/fluxos?destaque={fluxoId}",
            AutorId = gestorId,
        });
        await db.SaveChangesAsync();
        // Sem Teams aqui: o canal recebe só percurso completo do supervisionado.
    }

    return Results.NoContent();
})
   .WithName("AtribuirFluxo")
   .RequireAuthorization("Gestor");

// Desvincula (remove) um fluxo antes atribuído a um supervisionado do gestor.
app.MapDelete("/gestor/fluxos/{fluxoId:guid}/atribuir/{usuarioId:guid}", async (Guid fluxoId, Guid usuarioId, ClaimsPrincipal user, AppDbContext db) =>
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

    var atrib = await db.FluxosAtribuidos
        .FirstOrDefaultAsync(a => a.FluxoId == fluxoId && a.UsuarioId == usuarioId);
    if (atrib is not null)
    {
        db.FluxosAtribuidos.Remove(atrib);
        await db.SaveChangesAsync();
    }

    return Results.NoContent();
})
   .WithName("DesvincularFluxo")
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
    if (string.IsNullOrWhiteSpace(req.Senha) || req.Senha.Length < 6)
    {
        return Results.BadRequest(new { erro = "A senha precisa de ao menos 6 caracteres." });
    }
    if (await db.Usuarios.AnyAsync(u => u.Email == email))
    {
        return Results.BadRequest(new { erro = "Já existe uma conta com esse e-mail." });
    }

    var gestores = config.GetSection("Gestores").Get<string[]>() ?? [];
    var usuario = new Usuario
    {
        Nome = req.Nome.Trim(),
        Email = email!,
        SenhaHash = SenhaHasher.Hash(req.Senha),
        IsGestor = gestores.Any(g => string.Equals(g, email!.Value, StringComparison.OrdinalIgnoreCase)),
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

    // Reaplica o papel de gestor conforme o appsettings (caso a lista tenha mudado).
    var gestores = config.GetSection("Gestores").Get<string[]>() ?? [];
    var ehGestor = gestores.Any(g => string.Equals(g, email!.Value, StringComparison.OrdinalIgnoreCase));
    if (usuario.IsGestor != ehGestor)
    {
        usuario.IsGestor = ehGestor;
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
app.MapPut("/users/{id:guid}/perfil", async (Guid id, SalvarPerfilRequest req, AppDbContext db) =>
{
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
   .WithName("SalvarPerfil");

// Dados do usuário: perfil salvo + se já nivelou. O front usa no login pra decidir se pula o
// questionário e monta a trilha direto.
app.MapGet("/users/{id:guid}", async (Guid id, AppDbContext db) =>
{
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
   .WithName("GetUsuario");

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
app.MapGet("/users/{id:guid}/progress", async (Guid id, AppDbContext db) =>
    await db.PassosConcluidos
        .Where(passo => passo.UsuarioId == id)
        .Select(passo => passo.OnboardingStepId)
        .ToListAsync())
   .WithName("GetProgresso");

// Marca um passo como concluído (idempotente).
app.MapPost("/users/{id:guid}/progress/{stepId:guid}", async (Guid id, Guid stepId, ConcluirPassoRequest? req, AppDbContext db) =>
{
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
            var step = await db.OnboardingSteps.FindAsync(stepId);
            if (step is not null)
            {
                var idsDaFase = await db.OnboardingSteps
                    .Where(s => s.Phase == step.Phase)
                    .Select(s => s.Id)
                    .ToListAsync();
                var concluidosDaFase = await db.PassosConcluidos
                    .CountAsync(p => p.UsuarioId == id && idsDaFase.Contains(p.OnboardingStepId));

                if (idsDaFase.Count > 0 && concluidosDaFase >= idsDaFase.Count)
                {
                    var msg = $"{usuario.Nome} concluiu a fase {step.Phase}.";
                    db.Notificacoes.Add(new Notificacao { UsuarioId = gestorId, Mensagem = msg, AutorId = id });
                    await db.SaveChangesAsync();
                    await teams.EnviarAsync(msg);
                }
            }
        }
    }

    return Results.NoContent();
})
   .WithName("ConcluirPasso");

// Desmarca um passo (toggle).
app.MapDelete("/users/{id:guid}/progress/{stepId:guid}", async (Guid id, Guid stepId, AppDbContext db) =>
{
    var passo = await db.PassosConcluidos
        .FirstOrDefaultAsync(p => p.UsuarioId == id && p.OnboardingStepId == stepId);

    if (passo is not null)
    {
        db.PassosConcluidos.Remove(passo);
        await db.SaveChangesAsync();
    }

    return Results.NoContent();
})
   .WithName("DesmarcarPasso");

// Comprovação (evidência) de um passo — a tela do passo usa pra pré-preencher o que já foi anexado.
app.MapGet("/users/{id:guid}/progress/{stepId:guid}", async (Guid id, Guid stepId, AppDbContext db) =>
{
    var registro = await db.PassosConcluidos
        .FirstOrDefaultAsync(p => p.UsuarioId == id && p.OnboardingStepId == stepId);
    return Results.Ok(new
    {
        Concluido = registro is not null,
        Evidencia = registro?.Evidencia ?? string.Empty,
    });
})
   .WithName("GetComprovacaoPasso");

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
