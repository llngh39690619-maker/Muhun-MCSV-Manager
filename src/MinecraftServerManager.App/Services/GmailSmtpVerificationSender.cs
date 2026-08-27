using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace MinecraftServerManager.App.Services;

internal interface IVerificationEmailSender
{
    Task SendVerificationCodeAsync(
        GmailSmtpCredential credential,
        string recipientGmail,
        string code,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken);
}

internal sealed class GmailSmtpVerificationSender : IVerificationEmailSender
{
    internal const string Host = "smtp.gmail.com";
    internal const int Port = 587;

    public async Task SendVerificationCodeAsync(
        GmailSmtpCredential credential,
        string recipientGmail,
        string code,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!MinecraftServerManager.Remote.RemoteIdentity.IsCanonicalGmailLogin(recipientGmail))
        {
            throw new InvalidOperationException("收件帳號必須是完整且有效的 @gmail.com 帳號。");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Muhun MCSV Manager", credential.SenderGmail));
        message.To.Add(MailboxAddress.Parse(recipientGmail));
        message.Subject = "Muhun MCSV Manager 遠端帳號驗證碼";
        message.Body = new TextPart("plain")
        {
            Text = $"您的 MCSV Manager 驗證碼是：{code}\r\n\r\n" +
                   $"此驗證碼將於 {expiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} 到期，且只能使用一次。\r\n" +
                   "如果您沒有在電腦端要求建立遠端帳號，請忽略此郵件。\r\n"
        };

        using var client = new SmtpClient
        {
            Timeout = 20_000
        };

        // StartTls requires the server to negotiate TLS successfully. MailKit's
        // normal platform certificate validation remains enabled; never accept an
        // invalid certificate or silently downgrade to clear text.
        await client.ConnectAsync(Host, Port, SecureSocketOptions.StartTls, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await client.AuthenticateAsync(
                    credential.SenderGmail,
                    credential.AppPassword,
                    cancellationToken)
                .ConfigureAwait(false);
            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // The message operation has already completed or failed. A disconnect
                    // failure must not replace its result or expose SMTP details to the UI.
                }
            }
        }
    }
}
