namespace Bussola.Domain.Entities;

// Registro de que um usuário concluiu um passo da jornada. (Usuario × OnboardingStep + quando.)
public class PassoConcluido
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsuarioId { get; set; }
    public Guid OnboardingStepId { get; set; }
    public DateTime ConcluidoEm { get; set; } = DateTime.UtcNow;
}
