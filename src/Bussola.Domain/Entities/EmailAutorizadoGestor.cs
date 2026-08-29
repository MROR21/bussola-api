namespace Bussola.Domain.Entities;

// E-mail pré-autorizado a virar gestor/supervisor. Um gestor existente cadastra o e-mail de alguém
// (que pode nem ter se cadastrado ainda) e, quando essa pessoa criar a conta (/auth/register), já
// nasce como gestor automaticamente — sem precisar de ninguém clicando um botão depois.
public class EmailAutorizadoGestor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
}
