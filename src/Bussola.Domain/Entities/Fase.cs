namespace Bussola.Domain.Entities;

// Uma fase da Jornada (ex.: "Ambientação", "Padrões"). Antes era só uma string solta em cada
// OnboardingStep; agora é entidade própria — o admin cria/renomeia/reordena pela tela.
public class Fase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = string.Empty;
    public int Order { get; set; }
}
