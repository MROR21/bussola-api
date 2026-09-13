namespace Bussola.Domain.Entities;

// Squad do colaborador (ex.: "Mão de Obra", "Quiz Quality"). Antes era um enum fixo de 3 valores
// (ver git history); agora é entidade própria — o admin cria/renomeia/reordena pela tela (mesmo
// padrão de Fase/Modulo, mesma forma Id/Nome/Order de propósito, pra reaproveitar o
// SimpleEntityCrud do front). Criar um Squad cria automaticamente um Modulo homônimo vinculado
// (ver POST /admin/squads) — um Modulo sem Squad (ex.: "Básico do dev") é um "padrão do sistema",
// criado à mão.
public class Squad
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = string.Empty;
    public int Order { get; set; }
}
