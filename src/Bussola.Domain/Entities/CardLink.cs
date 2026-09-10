namespace Bussola.Domain.Entities;

// O link do card que o gestor escolheu pro supervisionado (fase "Primeiro Card") — a AUSÊNCIA
// disso (nenhum registro pra esse UsuarioId) é o que trava os passos dessa fase pro colaborador;
// enviar de novo sobrescreve o mesmo registro (1 por pessoa), não cria histórico.
public class CardLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsuarioId { get; set; }
    public Guid EnviadoPorGestorId { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime EnviadoEm { get; set; } = DateTime.UtcNow;
}
