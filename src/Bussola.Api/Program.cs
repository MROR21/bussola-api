using Bussola.Domain.Nivelamento;
using Bussola.Infrastructure.Data;
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

var app = builder.Build();

// Ao iniciar: aplica migrations pendentes e semeia os dados iniciais (dev).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await OnboardingSeeder.SeedAsync(db);
}

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
        RecommendedDepth = TrailPlanner.DepthFor(step, perfil),
    });

    return Results.Ok(trail);
})
   .WithName("GetOnboardingTrail");

app.Run();
