namespace Bussola.Domain.Entities;

// Token pessoal (Personal Access Token) — deixa chamar a API do Bússola direto (curl/PowerShell/
// scripts) sem logar, com o MESMO acesso do dono do token (não é um papel à parte). Só o HASH é
// guardado (ver Auth/ApiTokenHasher.cs); o valor em texto puro só existe no momento da criação,
// devolvido uma única vez na resposta — igual GitHub/Jira/Bitbucket fazem.
public class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsuarioId { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? UltimoUsoEm { get; set; }
}
