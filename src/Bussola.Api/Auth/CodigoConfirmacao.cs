namespace Bussola.Api.Auth;

// Código numérico de 6 dígitos pra confirmar posse de um e-mail no cadastro por senha (ver
// EmailSender e o endpoint `/auth/confirmar-email` em Program.cs). `Random.Shared` é thread-safe
// e suficiente aqui — não é um segredo de longa duração, expira em minutos.
public static class CodigoConfirmacao
{
    public const int ValidoPorMinutos = 15;

    public static string Gerar() => Random.Shared.Next(0, 1_000_000).ToString("D6");
}
