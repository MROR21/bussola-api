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
    public DbSet<FluxoConcluido> FluxosConcluidos => Set<FluxoConcluido>();
    public DbSet<Fase> Fases => Set<Fase>();
    public DbSet<Modulo> Modulos => Set<Modulo>();
    public DbSet<EmailAutorizadoGestor> EmailsAutorizadosGestor => Set<EmailAutorizadoGestor>();

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

        // Um fluxo só pode ser concluído uma vez por usuário.
        modelBuilder.Entity<FluxoConcluido>()
            .HasIndex(fc => new { fc.UsuarioId, fc.FluxoId })
            .IsUnique();

        // Nome de fase/módulo é único — evita duplicata confusa quando o admin cria/renomeia.
        modelBuilder.Entity<Fase>()
            .HasIndex(f => f.Nome)
            .IsUnique();

        modelBuilder.Entity<Modulo>()
            .HasIndex(m => m.Nome)
            .IsUnique();

        // E-mail pré-autorizado é único — sem duplicata na lista.
        modelBuilder.Entity<EmailAutorizadoGestor>()
            .HasIndex(e => e.Email)
            .IsUnique();

        // Restrict (não Cascade, que seria o padrão do EF pra FK obrigatória): apagar uma Fase ou
        // Módulo com passos/fluxos vinculados deve falhar no banco — segunda trava além da checagem
        // que o endpoint de admin já faz, não apagar o conteúdo em cascata.
        modelBuilder.Entity<OnboardingStep>()
            .HasOne(s => s.Fase)
            .WithMany()
            .HasForeignKey(s => s.FaseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Fluxo>()
            .HasOne(f => f.Modulo)
            .WithMany()
            .HasForeignKey(f => f.ModuloId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
