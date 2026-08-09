using Bussola.Api.Auth;
using Bussola.Domain.Entities;
using Bussola.Domain.Nivelamento;
using Bussola.Domain.ValueObjects;
using Bussola.Infrastructure.Data;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

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

var app = builder.Build();

// Ao iniciar: aplica migrations pendentes e semeia os dados iniciais (dev).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await OnboardingSeeder.SeedAsync(db);
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

// --- Auth + Usuário + Progresso ---

// Login demo: get-or-create por email + emite JWT (token com expiração).
app.MapPost("/auth/login", async (LoginRequest req, AppDbContext db, TokenService tokens) =>
{
    // O Value Object valida o email: se não passar, nem chega no banco.
    if (!Email.TryCreate(req.Email, out var email))
    {
        return Results.BadRequest(new { erro = "Email inválido." });
    }

    var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Email == email);
    if (usuario is null)
    {
        usuario = new Usuario { Nome = req.Nome, Email = email! };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();
    }

    var (token, expiraEm) = tokens.Emitir(usuario);
    return Results.Ok(new
    {
        token,
        expiraEm,
        usuario = new { usuario.Id, usuario.Nome, Email = usuario.Email.Value, usuario.Cargo },
    });
})
   .WithName("Login");

// Salva o nivelamento (Perfil) no usuário.
app.MapPut("/users/{id:guid}/perfil", async (Guid id, Perfil perfil, AppDbContext db) =>
{
    var usuario = await db.Usuarios.FindAsync(id);
    if (usuario is null) return Results.NotFound(new { erro = "Usuário não encontrado." });

    usuario.Cargo = perfil.Cargo;
    usuario.Frontend = perfil.Frontend;
    usuario.Backend = perfil.Backend;
    usuario.Git = perfil.Git;
    usuario.Sql = perfil.Sql;
    usuario.Jira = perfil.Jira;
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

    return Results.Ok(new
    {
        usuario.Id,
        usuario.Nome,
        Email = usuario.Email.Value,
        usuario.Cargo,
        usuario.NivelamentoConcluido,
        perfil = usuario.ToPerfil(),
    });
})
   .WithName("GetUsuario");

// Lista os ids dos passos que o usuário já concluiu.
app.MapGet("/users/{id:guid}/progress", async (Guid id, AppDbContext db) =>
    await db.PassosConcluidos
        .Where(passo => passo.UsuarioId == id)
        .Select(passo => passo.OnboardingStepId)
        .ToListAsync())
   .WithName("GetProgresso");

// Marca um passo como concluído (idempotente).
app.MapPost("/users/{id:guid}/progress/{stepId:guid}", async (Guid id, Guid stepId, AppDbContext db) =>
{
    var jaConcluido = await db.PassosConcluidos
        .AnyAsync(passo => passo.UsuarioId == id && passo.OnboardingStepId == stepId);

    if (!jaConcluido)
    {
        db.PassosConcluidos.Add(new PassoConcluido { UsuarioId = id, OnboardingStepId = stepId });
        await db.SaveChangesAsync();
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

app.Run();

// Corpo do login (get-or-create por email).
record LoginRequest(string Nome, string Email);
