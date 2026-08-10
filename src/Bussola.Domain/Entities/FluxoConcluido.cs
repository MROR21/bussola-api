namespace Bussola.Domain.Entities;

// Marca que um usuário concluiu um fluxo. Enquanto não há vídeo, "concluir" = abrir e marcar.
public class FluxoConcluido
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsuarioId { get; set; }
    public Guid FluxoId { get; set; }
    public DateTime ConcluidoEm { get; set; } = DateTime.UtcNow;
}
