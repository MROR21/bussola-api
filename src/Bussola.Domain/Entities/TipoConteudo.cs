namespace Bussola.Domain.Entities;

// Distingue as duas abas do Guia dentro de um módulo: "Fluxos" (passo a passo do dia a dia) vs
// "Documentação" (referência mais formal, por squad). Mesma entidade/tabela Fluxo pros dois — só
// rótulo, sem duplicar estrutura.
public enum TipoConteudo
{
    Fluxo = 0,
    Documentacao = 1,
}
