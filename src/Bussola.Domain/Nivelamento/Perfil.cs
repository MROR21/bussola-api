using Bussola.Domain.Entities;

namespace Bussola.Domain.Nivelamento;

// Respostas do questionário: cargo + nível por área. Campos explícitos (JSON simples pro front).
public record Perfil(
    Cargo Cargo,
    SkillLevel Frontend = SkillLevel.Nenhum,
    SkillLevel Backend = SkillLevel.Nenhum,
    SkillLevel Git = SkillLevel.Nenhum,
    SkillLevel Sql = SkillLevel.Nenhum,
    SkillLevel Jira = SkillLevel.Nenhum)
{
    // Nível da pessoa numa área específica (usado pela regra de profundidade).
    public SkillLevel LevelFor(SkillArea area) => area switch
    {
        SkillArea.Frontend => Frontend,
        SkillArea.Backend => Backend,
        SkillArea.Git => Git,
        SkillArea.Sql => Sql,
        SkillArea.Jira => Jira,
        _ => SkillLevel.Nenhum,
    };
}
