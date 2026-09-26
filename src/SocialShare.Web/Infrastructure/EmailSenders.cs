using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using SocialShare.Core.Services;

namespace SocialShare.Web.Infrastructure;

/// <summary>
/// The default. Writes the mail to the log, which in development means the confirmation and
/// reset links land in the console where they are easy to click.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IAppEmailSender
{
    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Email not sent, no provider configured.\n  To: {To}\n  Subject: {Subject}\n  Body:\n{Body}",
            toEmail, subject, htmlBody);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Real mail. Registered instead of the logger when Email:Provider is set to SendGrid and an
/// API key is present, so switching providers is a configuration edit.
/// </summary>
public sealed class SendGridEmailSender(
    IOptions<EmailOptions> options,
    ILogger<SendGridEmailSender> logger) : IAppEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken)
    {
        var client = new SendGridClient(_options.SendGridApiKey);
        var message = MailHelper.CreateSingleEmail(
            new EmailAddress(_options.FromAddress, _options.FromName),
            new EmailAddress(toEmail),
            subject,
            plainTextContent: null,
            htmlContent: htmlBody);

        var response = await client.SendEmailAsync(message, cancellationToken);
        if ((int)response.StatusCode >= 400)
        {
            var body = await response.Body.ReadAsStringAsync(cancellationToken);
            logger.LogError("SendGrid refused the message to {To}: {Status} {Body}",
                toEmail, (int)response.StatusCode, body);
        }
    }
}
