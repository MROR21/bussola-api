namespace Bussola.Domain.Entities;

public class OnboardingStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    public string Phase { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCompanySpecific { get; set; }
    // Área que o passo cobre (só faz sentido em passo genérico). None = não-nivelável / sempre essencial.
    public SkillArea SkillArea { get; set; } = SkillArea.None;

    // Conteúdo completo do passo, em Markdown (a "aula"). O Description é o resumo de uma linha.
    public string Conteudo { get; set; } = string.Empty;
}
