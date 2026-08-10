using Bussola.Domain.Nivelamento;
using Bussola.Domain.ValueObjects;

namespace Bussola.Domain.Entities;

// Usuário do onboarding. Guarda o Perfil (respostas do nivelamento) e a lista de passos concluídos.
public class Usuario
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = string.Empty;

    // Email é um Value Object: required força setar na criação (nunca fica num estado inválido).
    public required Email Email { get; set; }

    // Perfil persistido (mesmos campos do record Perfil, achatados em colunas).
    public Cargo Cargo { get; set; } = Cargo.Estagiario;
    public SkillLevel Frontend { get; set; } = SkillLevel.Nenhum;
    public SkillLevel Backend { get; set; } = SkillLevel.Nenhum;
    public SkillLevel Git { get; set; } = SkillLevel.Nenhum;
    public SkillLevel Sql { get; set; } = SkillLevel.Nenhum;
    public SkillLevel Jira { get; set; } = SkillLevel.Nenhum;

    // True depois que o usuário responde (ou pula) o nivelamento. Distingue "respondeu tudo Nenhum"
    // de "ainda não respondeu" — o front usa pra pular o questionário e ir direto pra trilha.
    public bool NivelamentoConcluido { get; set; }

    // Papel de gestor (definido no login pela lista de e-mails no appsettings). Libera o painel do gestor.
    public bool IsGestor { get; set; }

    public List<PassoConcluido> PassosConcluidos { get; set; } = [];

    // Monta o record Perfil (value object usado pelo TrailPlanner) a partir das colunas.
    public Perfil ToPerfil() => new(Cargo, Frontend, Backend, Git, Sql, Jira);
}
