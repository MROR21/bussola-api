namespace Bussola.Domain.Entities;

// Marca que um Acesso já foi liberado por um supervisionado específico. Quem marca é o GESTOR
// dele, clicando o chip na tela de detalhe do supervisionado — não tem como o sistema saber que a
// pessoa "voltou" de um link externo, então marca já na hora do clique.
public class AcessoConcluido
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsuarioId { get; set; }
    public Guid AcessoId { get; set; }
    public DateTime ConcluidoEm { get; set; } = DateTime.UtcNow;
}
