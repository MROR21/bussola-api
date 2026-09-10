using System.Security.Cryptography;
using System.Text;

namespace Bussola.Api.Auth;

// Geração e hash do token de API. Diferente da senha (SenhaHasher, PBKDF2 com salt por senha —
// necessário porque senha tem baixa entropia e precisa resistir a rainbow table), o token já
// nasce com 256 bits aleatórios: dá pra usar um hash determinístico (SHA256) e achar o registro
// direto por `WHERE TokenHash = ...`, sem iterar e comparar um a um.
public static class ApiTokenHasher
{
    public const string Prefixo = "bussola_pat_";

    public static string Gerar()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var valor = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return Prefixo + valor;
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
