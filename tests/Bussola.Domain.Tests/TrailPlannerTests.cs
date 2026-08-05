using Bussola.Domain.Entities;
using Bussola.Domain.Nivelamento;
using Xunit;

namespace Bussola.Domain.Tests;

// Testa a regra de profundidade do nivelamento. Função pura -> sem banco, sem HTTP, rápido.
public class TrailPlannerTests
{
    // Perfil de quem já é confortável em git (o pior caso pra "encolher" indevidamente).
    private static Perfil ConfortavelEmGit(Cargo cargo = Cargo.Junior) =>
        new(cargo, Git: SkillLevel.Confortavel);

    [Fact]
    public void PassoEspecificoAgilean_SempreEssencial_MesmoParaExpert()
    {
        var passo = new OnboardingStep { IsCompanySpecific = true, SkillArea = SkillArea.Git };

        Assert.Equal(StepDepth.Essencial, TrailPlanner.DepthFor(passo, ConfortavelEmGit()));
    }

    [Fact]
    public void PassoSemArea_SempreEssencial()
    {
        var passo = new OnboardingStep { IsCompanySpecific = false, SkillArea = SkillArea.None };

        Assert.Equal(StepDepth.Essencial, TrailPlanner.DepthFor(passo, ConfortavelEmGit()));
    }

    [Fact]
    public void PassoGitGenerico_ParaQuemEhConfortavel_Resumo()
    {
        var passo = new OnboardingStep { IsCompanySpecific = false, SkillArea = SkillArea.Git };

        Assert.Equal(StepDepth.Resumo, TrailPlanner.DepthFor(passo, ConfortavelEmGit()));
    }

    [Fact]
    public void PassoGitGenerico_ParaQuemSoTemBasico_Essencial()
    {
        var passo = new OnboardingStep { IsCompanySpecific = false, SkillArea = SkillArea.Git };
        var perfil = new Perfil(Cargo.Estagiario, Git: SkillLevel.Basico);

        Assert.Equal(StepDepth.Essencial, TrailPlanner.DepthFor(passo, perfil));
    }
}
