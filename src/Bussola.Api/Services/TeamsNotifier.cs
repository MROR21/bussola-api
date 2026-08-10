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
            // Cartão adaptável — formato que o fluxo "Enviar alertas de webhook para canal" renderiza.
            var payload = new
            {
                type = "message",
                attachments = new[]
                {
                    new
                    {
                        contentType = "application/vnd.microsoft.card.adaptive",
                        content = new
                        {
                            type = "AdaptiveCard",
                            version = "1.4",
                            body = new object[]
                            {
                                new
                                {
                                    type = "TextBlock",
                                    text = "🧭 Bússola",
                                    weight = "Bolder",
                                    color = "Accent",
                                    size = "Small",
                                    spacing = "None",
                                },
                                new { type = "TextBlock", text = mensagem, wrap = true, size = "Medium" },
                            },
                        },
                    },
                },
            };
            await client.PostAsJsonAsync(url, payload);
        }
        catch (Exception e)
        {
            // Não deixa a falha do Teams quebrar o fluxo principal (a notificação in-app já foi salva).
            logger.LogWarning(e, "Falha ao enviar notificação pro Teams");
        }
    }
}
