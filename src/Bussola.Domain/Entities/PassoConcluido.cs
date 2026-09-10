namespace Bussola.Domain.Entities;

// Registro de que um usuário concluiu um passo da jornada. (Usuario × OnboardingStep + quando.)
public class PassoConcluido
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsuarioId { get; set; }
    public Guid OnboardingStepId { get; set; }
    public DateTime ConcluidoEm { get; set; } = DateTime.UtcNow;

    // Comprovação opcional do passo: link do PR, um print (URL) ou uma nota. Vazio = sem comprovação.
    public string Evidencia { get; set; } = string.Empty;

    // O gestor revisou o PR (comentários ficam no Bitbucket mesmo, isso aqui é só o status) e
    // pediu ajuste — true até o PRÓPRIO colaborador marcar que corrigiu (não é um toggle livre:
    // só o gestor liga, só o colaborador desliga, ver os 2 endpoints em Program.cs).
    public bool PrecisaCorrecao { get; set; }

    // Quantas vezes esse ciclo (pedir correção → colaborador corrigir) já se fechou — incrementa
    // toda vez que o colaborador marca "corrigido". "Pedir correção" continua disponível pro
    // gestor mesmo depois de um ciclo fechado (pode pedir de novo), esse número é só o histórico.
    public int QtdCorrecoes { get; set; }

    // O colaborador marcou como corrigido, mas o GESTOR ainda não confirmou que revisou/aprovou —
    // true até o gestor conferir e marcar como concluído (ou pedir correção de novo, se não
    // resolveu). Existe separado de PrecisaCorrecao pra não confundir "corrigido pelo colaborador"
    // com "confirmado pelo gestor" — são pessoas e momentos diferentes.
    public bool AguardandoConfirmacao { get; set; }
}
