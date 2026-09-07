using Bussola.Domain.Nivelamento;

namespace Bussola.Domain.Entities;

// Um acesso que a Agilean precisa liberar pra um novato (ex.: "E-mail Agilean", "Teams") — o admin
// cria/edita pela tela, com um link direto pra página que libera ele de verdade. `CargoMinimo` é
// cumulativo (mesmo espírito da lista ilustrativa que isso substitui, ACESSOS_POR_CARGO no front):
// quem tem esse cargo OU um acima também precisa desse acesso — Estagiario < Junior < Pleno.
public class Acesso
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public Cargo CargoMinimo { get; set; }
    public int Order { get; set; }
}
