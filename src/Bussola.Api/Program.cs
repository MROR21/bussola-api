using Bussola.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI (Swagger) — https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Banco (Postgres via EF Core)
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// CORS liberando o front (Vite dev)
const string FrontCors = "front";
builder.Services.AddCors(options =>
    options.AddPolicy(FrontCors, policy =>
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
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

app.Run();
