using Bussola.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bussola.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<OnboardingStep> OnboardingSteps => Set<OnboardingStep>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<PassoConcluido> PassosConcluidos => Set<PassoConcluido>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
