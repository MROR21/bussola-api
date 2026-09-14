namespace Bussola.Domain.Entities;

// Um módulo do Guia pelo sistema (ex.: "Mão de Obra", "Básico do dev"). Antes era só uma string
// solta em cada Fluxo; agora é entidade própria — o admin cria/renomeia/reordena pela tela.
public class Modulo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = string.Empty;
    public int Order { get; set; }
    // Squad ao qual este módulo corresponde (ex.: módulo "Mão de Obra" ↔ squad "Mão de Obra").
    // null = módulo "padrão" sem squad própria (ex.: "Básico do dev") — criado à mão pelo admin,
    // nunca por um squad. Preenchido automaticamente só quando o módulo nasce junto de um squad
    // novo (ver POST /admin/squads).
    public Guid? SquadId { get; set; }
    public virtual Squad? Squad { get; set; }

    // Nome de um ícone do material-symbols (ex.: "engineering") — escolhido pelo admin na criação
    // ou edição (ver ModuloIcones.cs no front pra lista curada). Antes era um dicionário fixo por
    // NOME de módulo no código (GuiasPage.tsx); módulo novo (que não estivesse nesse dicionário)
    // sempre caía num ícone genérico de quebra-cabeça, sem jeito de corrigir sem alterar código.
    public string Icone { get; set; } = "inventory_2";
}
