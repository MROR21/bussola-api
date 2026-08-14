namespace Bussola.Domain.Entities;

// Um módulo do Guia pelo sistema (ex.: "Mão de Obra", "Básico do dev"). Antes era só uma string
// solta em cada Fluxo; agora é entidade própria — o admin cria/renomeia/reordena pela tela.
public class Modulo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = string.Empty;
    public int Order { get; set; }
}
