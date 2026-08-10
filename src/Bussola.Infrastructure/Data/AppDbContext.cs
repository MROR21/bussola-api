using Bussola.Domain.Entities;
using Bussola.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Bussola.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<OnboardingStep> OnboardingSteps => Set<OnboardingStep>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<PassoConcluido> PassosConcluidos => Set<PassoConcluido>();
    public DbSet<Fluxo> Fluxos => Set<Fluxo>();
    public DbSet<Notificacao> Notificacoes => Set<Notificacao>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Email é um Value Object: grava a string na coluna e reidrata pro VO na leitura.
        // A coluna continua "text" — só muda o tipo em C#, sem alterar o schema.
        modelBuilder.Entity<Usuario>()
            .Property(usuario => usuario.Email)
            .HasConversion(email => email.Value, value => Email.Create(value));

        // Email é a chave de login (get-or-create), então único.
        modelBuilder.Entity<Usuario>()
            .HasIndex(usuario => usuario.Email)
            .IsUnique();

        // Um passo só pode ser concluído uma vez por usuário.
        modelBuilder.Entity<PassoConcluido>()
            .HasIndex(passo => new { passo.UsuarioId, passo.OnboardingStepId })
            .IsUnique();
    }
}
