using MailKit.Net.Smtp;
using MimeKit;

namespace TodoSaaS.Api.Services;

public interface IEmailService
{
    Task SendEmailAsync(string toEmail, string subject, string htmlBody);
}

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        try
        {
            var host = _config["Email:Host"] ?? "mailpit";
            var port = int.TryParse(_config["Email:Port"], out var p) ? p : 1025;
            var senderEmail = _config["Email:Sender"] ?? "noreply@todosass.com";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("TodoSaaS", senderEmail));
            message.To.Add(new MailboxAddress("", toEmail));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder { HtmlBody = htmlBody };
            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();
            // Disconnect if no SSL certificate locally (Mailpit doesn't use SSL)
            await client.ConnectAsync(host, port, MailKit.Security.SecureSocketOptions.Auto);

            var user = _config["Email:Username"];
            var pass = _config["Email:Password"];
            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
            {
                await client.AuthenticateAsync(user, pass);
            }

            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("Email sent successfully to {ToEmail} with subject: {Subject}", toEmail, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {ToEmail}", toEmail);
        }
    }
}
