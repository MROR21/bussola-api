namespace Bussola.Domain.Entities;

// Notificação in-app para um usuário (o sininho). Também é a camada de evento que alimenta o Teams.
public class Notificacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsuarioId { get; set; } // destinatário
    // Autor/sujeito da notificação — só pra mostrar avatar + nome no sino. Ex.: o gestor que
    // atribuiu (na notif do supervisionado) ou o supervisionado que avançou (na notif do gestor).
    public Guid? AutorId { get; set; }
    public string Mensagem { get; set; } = string.Empty;
    // Rota do front pra onde a notificação leva ao ser clicada (vazio = não navega).
    public string Link { get; set; } = string.Empty;
    public bool Lida { get; set; }
    public DateTime CriadaEm { get; set; } = DateTime.UtcNow;
}
