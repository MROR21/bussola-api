using Bussola.Domain.Nivelamento;

namespace Bussola.Domain.Entities;

// Um fluxo do dia a dia (a "Referência viva"): consultável a qualquer momento, fora da jornada.
public class Fluxo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    // Módulo = agrupamento por squad/área ("Mão de Obra", "Básico do dev"). Organiza a Referência.
    public Guid ModuloId { get; set; }
    public virtual Modulo Modulo { get; set; } = null!;
    // Squad ao qual o fluxo pertence. null = vale pra todos (ex.: "Básico do dev").
    public Squad? Squad { get; set; }
    public string Categoria { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;
    public string Conteudo { get; set; } = string.Empty;
    // URL do vídeo do fluxo (opcional). Vazio = fluxo só de texto.
    public string VideoUrl { get; set; } = string.Empty;
}
