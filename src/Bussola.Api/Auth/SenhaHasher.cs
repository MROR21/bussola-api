using System.Security.Cryptography;

namespace Bussola.Api.Auth;

// Hash de senha com PBKDF2 (SHA256 + salt aleatório por senha). Sem dependência externa.
// Formato armazenado: "{salt}.{hash}" em base64.
public static class SenhaHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iteracoes = 100_000;

    public static string Hash(string senha)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(senha, salt, Iteracoes, HashAlgorithmName.SHA256, HashBytes);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verificar(string senha, string armazenado)
    {
        var partes = armazenado.Split('.');
        if (partes.Length != 2)
        {
            return false;
        }

        var salt = Convert.FromBase64String(partes[0]);
        var esperado = Convert.FromBase64String(partes[1]);
        var hash = Rfc2898DeriveBytes.Pbkdf2(senha, salt, Iteracoes, HashAlgorithmName.SHA256, HashBytes);
        return CryptographicOperations.FixedTimeEquals(hash, esperado);
    }
}
