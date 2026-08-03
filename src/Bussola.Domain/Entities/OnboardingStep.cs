namespace Bussola.Domain.Entities;

public class OnboardingStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    public string Phase { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCompanySpecific { get; set; }
    public string? SkillTag { get; set; }
}
