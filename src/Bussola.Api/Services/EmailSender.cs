using System.Net;
using System.Net.Mail;

namespace Bussola.Api.Services;

// Envia e-mails transacionais (confirmação de cadastro) via SMTP AUTH — a própria caixa da
// Agilean (Office 365: smtp.office365.com:587, STARTTLS). Sem credencial configurada
// (Smtp:User/Smtp:Password) = no-op que só loga o conteúdo — modo mock, custo zero pro PDI, mesmo
// padrão do TeamsNotifier.
public class EmailSender(IConfiguration config, ILogger<EmailSender> logger)
{
    public async Task EnviarAsync(string destinatario, string assunto, string corpo)
    {
        var host = config["Smtp:Host"];
        var usuario = config["Smtp:User"];
        var senha = config["Smtp:Password"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(senha))
        {
            logger.LogInformation(
                "[E-mail mock] Para: {Destinatario} | Assunto: {Assunto}\n{Corpo}", destinatario, assunto, corpo);
            return;
        }

        var porta = config.GetValue<int?>("Smtp:Port") ?? 587;
        var remetente = config["Smtp:From"] is { Length: > 0 } from ? from : usuario;

        using var mensagem = new MailMessage(remetente, destinatario, assunto, corpo);
        using var cliente = new SmtpClient(host, porta)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(usuario, senha),
        };
        try
        {
            await cliente.SendMailAsync(mensagem);
        }
        catch (Exception e)
        {
            // Diferente do TeamsNotifier (notificação, não-crítico): aqui o e-mail É o fluxo — loga
            // como erro (não warning) pra ficar visível, mas quem chamou decide o que fazer com a
            // falha (endpoints de cadastro/reenvio já respondem com sucesso mesmo assim, pra não
            // travar o cadastro por causa de uma falha de SMTP — a pessoa pode pedir reenvio).
            logger.LogError(e, "Falha ao enviar e-mail para {Destinatario}", destinatario);
        }
    }
}
