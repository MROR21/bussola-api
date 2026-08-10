namespace Bussola.Domain.Entities;

// Fluxo liberado por um gestor a um supervisionado (além dos fluxos padrão do squad dele).
public class FluxoAtribuido
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FluxoId { get; set; }
    public Guid UsuarioId { get; set; } // o supervisionado que recebeu o fluxo
    public DateTime AtribuidoEm { get; set; } = DateTime.UtcNow;
}
