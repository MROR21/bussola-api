using Bussola.Domain.Entities;

namespace Bussola.Domain.Nivelamento;

// Decide a profundidade de cada passo conforme o perfil. Função PURA (sem banco/HTTP) -> testável.
public static class TrailPlanner
{
    public static StepDepth DepthFor(OnboardingStep step, Perfil perfil)
    {
        // Específico-Agilean ou não-nivelável (None): sempre essencial, nunca encolhe.
        if (step.IsCompanySpecific || step.SkillArea == SkillArea.None)
            return StepDepth.Essencial;

        // Genérico: quem já é confortável na área recebe o resumo; senão, essencial.
        return perfil.LevelFor(step.SkillArea) == SkillLevel.Confortavel
            ? StepDepth.Resumo
            : StepDepth.Essencial;
    }
}
