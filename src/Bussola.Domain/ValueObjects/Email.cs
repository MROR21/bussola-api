using System.Text.RegularExpressions;

namespace Bussola.Domain.ValueObjects;

// Value Object: um Email só existe se for válido. A validação mora AQUI, num lugar só, e o
// construtor é privado — ninguém cria um Email inválido por fora. Sendo record, a igualdade é
// por valor: dois Email com o mesmo texto são iguais.
public sealed record Email
{
    // Regex simples "algo@algo.algo" (sem espaços). Não é RFC completo de propósito — pega o grosso
    // sem virar uma fonte de bug. Endurecer depois é fácil, porque a regra está só aqui.
    private static readonly Regex Padrao = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public string Value { get; }

    // Privado: a única porta de entrada é o Create/TryCreate, que valida antes de construir.
    private Email(string value) => Value = value;

    // Tenta criar. Devolve false (e email = null) se inválido — o caller decide o que fazer
    // (a API, por exemplo, responde 400). Sem exceção pra validar entrada do usuário.
    public static bool TryCreate(string? input, out Email? email)
    {
        email = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var normalizado = input.Trim().ToLowerInvariant();
        if (!Padrao.IsMatch(normalizado))
        {
            return false;
        }

        email = new Email(normalizado);
        return true;
    }

    // Cria ou estoura. Use quando a validade já é garantida (seed, reidratação do banco).
    public static Email Create(string input) =>
        TryCreate(input, out var email)
            ? email!
            : throw new ArgumentException($"Email inválido: '{input}'.", nameof(input));

    public override string ToString() => Value;
}
