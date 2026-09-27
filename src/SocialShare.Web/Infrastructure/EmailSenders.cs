using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using SocialShare.Core.Services;

namespace SocialShare.Web.Infrastructure;

/// <summary>
/// Writes the message to the log instead of sending it. In development that is what puts the
/// confirmation and reset links in the console where they are easy to click.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IAppEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        // The plain text body is logged rather than the HTML, because it is the readable one
        // and the link in it is the thing anybody reading this log actually wants.
        logger.LogInformation(
            "Email not sent, Email:Provider is Log.\n  To: {To}\n  Subject: {Subject}\n{Body}",
            message.To, message.Subject, message.TextBody);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Real mail through SendGrid. Registered instead of <see cref="LogEmailSender"/> when
/// Email:Provider is SendGrid, which is validated at startup rather than fallen back from.
///
/// Docs: https://www.twilio.com/docs/sendgrid/api-reference/mail-send/mail-send
/// </summary>
public sealed class SendGridEmailSender(
    IHttpClientFactory httpClientFactory,
    IOptions<EmailOptions> options,
    ILogger<SendGridEmailSender> logger) : IAppEmailSender
{
    public const string HttpClientName = "sendgrid";

    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        // SendGridClient takes an HttpClient, so the connection is pooled by the factory and a
        // test can put its own handler underneath and read the exact payload.
        var client = new SendGridClient(
            httpClientFactory.CreateClient(HttpClientName),
            _options.SendGridApiKey,
            host: string.IsNullOrWhiteSpace(_options.SendGridHost) ? null : _options.SendGridHost);

        var mail = MailHelper.CreateSingleEmail(
            new EmailAddress(_options.FromAddress, _options.FromName),
            new EmailAddress(message.To),
            message.Subject,
            plainTextContent: message.TextBody,
            htmlContent: message.HtmlBody);

        if (!string.IsNullOrWhiteSpace(_options.ReplyToAddress))
        {
            mail.SetReplyTo(new EmailAddress(_options.ReplyToAddress));
        }

        // SendGrid rewrites every link for click tracking by default. On a confirmation email
        // that turns the URL into a redirector on a SendGrid domain, which looks like phishing
        // to the person receiving it and to plenty of spam filters. Off for both parts.
        mail.SetClickTracking(false, false);
        mail.SetOpenTracking(false, null);

        if (_options.SandboxMode)
        {
            // Validated by SendGrid and then thrown away. Proves credentials and the verified
            // sender without emailing anybody.
            mail.SetSandBoxMode(true);
        }

        Response response;
        try
        {
            response = await client.SendEmailAsync(mail, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new EmailSendException(
                $"Could not reach SendGrid to send '{message.Subject}' to {message.To}. {ex.Message}", ex);
        }

        var status = (int)response.StatusCode;
        if (status < 400)
        {
            logger.LogInformation(
                "Sent '{Subject}' to {To} through SendGrid, HTTP {Status}.{Sandbox}",
                message.Subject, message.To, status,
                _options.SandboxMode ? " Sandbox mode, so nothing was delivered." : string.Empty);
            return;
        }

        var body = await ReadBodyAsync(response, cancellationToken);

        // Never swallowed. A confirmation email that fails silently leaves an account that can
        // never sign in, and nobody finds out until somebody complains.
        throw new EmailSendException(
            $"SendGrid refused '{message.Subject}' to {message.To} with HTTP {status}. {body}".Trim());
    }

    private static async Task<string> ReadBodyAsync(Response response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Body.ReadAsStringAsync(cancellationToken);
            return body.Length > 1000 ? body[..1000] + "..." : body;
        }
        catch (Exception ex)
        {
            return $"(could not read the response body: {ex.Message})";
        }
    }
}
