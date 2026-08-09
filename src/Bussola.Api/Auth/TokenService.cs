using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Bussola.Domain.Entities;
using Microsoft.IdentityModel.Tokens;

namespace Bussola.Api.Auth;

// Emite um JWT assinado com expiração para o usuário logado.
// (Por ora só EMITE — a validação/proteção de endpoints entra junto do painel do gestor.)
public class TokenService(IConfiguration config)
{
    public (string token, DateTime expiraEm) Emitir(Usuario usuario)
    {
        var key = config["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key não configurada");
        var issuer = config["Jwt:Issuer"];
        var expiraEm = DateTime.UtcNow.AddMinutes(config.GetValue("Jwt:ExpMinutes", 120));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, usuario.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, usuario.Email),
            new("nome", usuario.Nome),
        ];

        var jwt = new JwtSecurityToken(
            issuer: issuer,
            audience: issuer,
            claims: claims,
            expires: expiraEm,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(jwt), expiraEm);
    }
}
