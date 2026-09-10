using System.Security.Claims;
using System.Text.Encodings.Web;
using Bussola.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bussola.Api.Auth;

// Autentica requests que mandam um token pessoal (gerado em /perfil/api-tokens) no lugar do JWT
// normal — mesmo uso que já fazemos com token do Jira/Bitbucket: dá pra chamar a API do Bússola
// direto via curl/PowerShell sem precisar logar toda vez. O token tem o MESMO acesso do dono (é
// ele, só sem senha) — por isso monta os MESMOS claims que o login por senha/Microsoft gera
// (`sub`/`gestor`/`nome`), assim toda policy/endpoint que já existe funciona sem precisar mudar
// nada neles. `gestor` sai sempre fresco do banco aqui (nunca fica desatualizado feito o JWT podia
// antes do fix de revalidação, porque não existe token de longa duração pra esse claim ficar velho
// — cada chamada já busca o `IsGestor` atual).
public class ApiTokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefixoHeader = "Bearer ";
        if (!header.StartsWith(prefixoHeader + ApiTokenHasher.Prefixo, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header[prefixoHeader.Length..];
        var hash = ApiTokenHasher.Hash(token);
        var registro = await db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (registro is null)
        {
            return AuthenticateResult.Fail("Token inválido.");
        }

        var usuario = await db.Usuarios.FindAsync(registro.UsuarioId);
        if (usuario is null || !usuario.Ativo)
        {
            return AuthenticateResult.Fail("Token inválido.");
        }

        registro.UltimoUsoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var claims = new[]
        {
            new Claim("sub", usuario.Id.ToString()),
            new Claim("gestor", usuario.IsGestor ? "true" : "false"),
            new Claim("nome", usuario.Nome),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
