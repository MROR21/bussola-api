namespace Bussola.Domain.Entities;

// Área de conhecimento que um passo GENÉRICO cobre. O nivelamento usa isso pra decidir a
// profundidade do passo (essencial vs resumo) conforme o que a pessoa já domina.
// Passo específico-Agilean (IsCompanySpecific = true) usa None e é sempre essencial.
public enum SkillArea
{
    None = 0,
    Frontend = 1,
    Backend = 2,
    Git = 3,
    Sql = 4,
    Jira = 5,
}
