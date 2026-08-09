namespace Bussola.Domain.Entities;

// Um fluxo do dia a dia (a "Referência viva"): consultável a qualquer momento, fora da jornada.
public class Fluxo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    public string Categoria { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;
    public string Conteudo { get; set; } = string.Empty;
}
