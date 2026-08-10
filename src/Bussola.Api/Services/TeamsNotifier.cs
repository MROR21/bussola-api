using System.Net.Http.Json;

namespace Bussola.Api.Services;

// Entrega os eventos (as mesmas mensagens das notificações) num canal do Teams via Incoming Webhook.
// Sem URL configurada (Teams:WebhookUrl) = no-op que só loga — modo mock, custo zero pro PDI.
public class TeamsNotifier(IConfiguration config, IHttpClientFactory httpFactory, ILogger<TeamsNotifier> logger)
{
    public async Task EnviarAsync(string mensagem)
    {
        var url = config["Teams:WebhookUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogInformation("[Teams mock] {Mensagem}", mensagem);
            return;
        }

        try
        {
            var client = httpFactory.CreateClient();
            // Payload simples aceito pelo Incoming Webhook do Teams (renderiza como card de texto).
            await client.PostAsJsonAsync(url, new { text = mensagem });
        }
        catch (Exception e)
        {
            // Não deixa a falha do Teams quebrar o fluxo principal (a notificação in-app já foi salva).
            logger.LogWarning(e, "Falha ao enviar notificação pro Teams");
        }
    }
}
