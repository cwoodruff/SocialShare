using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SocialShare.Core.Services;
using SocialShare.Tests.Support;
using SocialShare.Web.Infrastructure;

namespace SocialShare.Tests;

/// <summary>
/// SendGridClient takes an HttpClient, so the exact payload SendGrid would receive is readable
/// here without a network call or an API key.
/// </summary>
public class SendGridEmailSenderTests
{
    private static readonly EmailMessage Message = new(
        To: "woody@example.test",
        Subject: "Confirm your SocialShare account",
        HtmlBody: "<p>Confirm by <a href=\"https://app.test/confirm-email?code=abc\">clicking here</a>.</p>",
        TextBody: "Confirm by opening this link:\n\nhttps://app.test/confirm-email?code=abc");

    private static EmailOptions Options(Action<EmailOptions>? tweak = null)
    {
        var options = new EmailOptions
        {
            Provider = nameof(EmailProvider.SendGrid),
            SendGridApiKey = "SG.test-key",
            FromAddress = "no-reply@woodruff.dev",
            FromName = "SocialShare"
        };

        tweak?.Invoke(options);
        return options;
    }

    private static SendGridEmailSender NewSender(StubHttpMessageHandler stub, EmailOptions options) =>
        new(stub.AsFactory(),
            new OptionsWrapper<EmailOptions>(options),
            NullLogger<SendGridEmailSender>.Instance);

    [Fact]
    public async Task A_sent_message_carries_both_bodies_and_the_verified_sender()
    {
        var stub = new StubHttpMessageHandler().RespondText("mail/send", "", HttpStatusCode.Accepted);

        await NewSender(stub, Options()).SendAsync(Message, CancellationToken.None);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.sendgrid.com/v3/mail/send", request.Uri.ToString());
        Assert.Equal("Bearer SG.test-key", request.Headers.Authorization);

        var payload = JsonDocument.Parse(request.Body).RootElement;

        Assert.Equal("no-reply@woodruff.dev", payload.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal("SocialShare", payload.GetProperty("from").GetProperty("name").GetString());
        // MailHelper puts the recipient and the subject inside personalizations, not at the top.
        var personalization = payload.GetProperty("personalizations")[0];
        Assert.Equal("woody@example.test", personalization.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("Confirm your SocialShare account", personalization.GetProperty("subject").GetString());

        // Both parts, plain text first, which is the order SendGrid wants.
        var content = payload.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, content.Count);
        Assert.Equal("text/plain", content[0].GetProperty("type").GetString());
        Assert.Contains("https://app.test/confirm-email?code=abc", content[0].GetProperty("value").GetString());
        Assert.Equal("text/html", content[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Click_tracking_is_turned_off_so_the_confirmation_link_stays_itself()
    {
        var stub = new StubHttpMessageHandler().RespondText("mail/send", "", HttpStatusCode.Accepted);

        await NewSender(stub, Options()).SendAsync(Message, CancellationToken.None);

        var tracking = JsonDocument.Parse(stub.Requests.Single().Body)
            .RootElement.GetProperty("tracking_settings");

        // With click tracking on, SendGrid rewrites the confirmation URL to a redirector on its
        // own domain, which reads as phishing to the recipient and to spam filters.
        Assert.False(tracking.GetProperty("click_tracking").GetProperty("enable").GetBoolean());
        Assert.False(tracking.GetProperty("open_tracking").GetProperty("enable").GetBoolean());
    }

    [Fact]
    public async Task A_reply_to_address_is_included_when_one_is_configured()
    {
        var stub = new StubHttpMessageHandler().RespondText("mail/send", "", HttpStatusCode.Accepted);

        await NewSender(stub, Options(o => o.ReplyToAddress = "woody@woodruff.dev"))
            .SendAsync(Message, CancellationToken.None);

        var payload = JsonDocument.Parse(stub.Requests.Single().Body).RootElement;
        Assert.Equal("woody@woodruff.dev", payload.GetProperty("reply_to").GetProperty("email").GetString());
    }

    [Fact]
    public async Task Sandbox_mode_asks_sendgrid_to_validate_and_not_deliver()
    {
        var stub = new StubHttpMessageHandler().RespondText("mail/send", "", HttpStatusCode.Accepted);

        await NewSender(stub, Options(o => o.SandboxMode = true)).SendAsync(Message, CancellationToken.None);

        var payload = JsonDocument.Parse(stub.Requests.Single().Body).RootElement;
        Assert.True(payload.GetProperty("mail_settings").GetProperty("sandbox_mode").GetProperty("enable").GetBoolean());
    }

    [Fact]
    public async Task A_refused_message_throws_with_the_reason_sendgrid_gave()
    {
        var stub = new StubHttpMessageHandler().RespondText(
            "mail/send",
            """{"errors":[{"message":"The from address does not match a verified Sender Identity.","field":"from"}]}""",
            HttpStatusCode.Forbidden);

        var sender = NewSender(stub, Options());

        var ex = await Assert.ThrowsAsync<EmailSendException>(
            () => sender.SendAsync(Message, CancellationToken.None));

        // Never swallowed: a silently failed confirmation email strands the account it belongs to.
        Assert.Contains("403", ex.Message);
        Assert.Contains("verified Sender Identity", ex.Message);
        Assert.Contains("woody@example.test", ex.Message);
    }

    [Fact]
    public async Task A_transport_failure_throws_rather_than_looking_like_a_success()
    {
        var stub = new StubHttpMessageHandler();
        stub.Throw("mail/send", new HttpRequestException("Name or service not known"));

        var sender = NewSender(stub, Options());

        var ex = await Assert.ThrowsAsync<EmailSendException>(
            () => sender.SendAsync(Message, CancellationToken.None));

        Assert.Contains("Could not reach SendGrid", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task The_log_sender_never_throws_and_writes_the_readable_body()
    {
        var log = new CapturingLogger<LogEmailSender>();

        await new LogEmailSender(log).SendAsync(Message, CancellationToken.None);

        var line = Assert.Single(log.Messages);
        Assert.Contains("woody@example.test", line);
        Assert.Contains("https://app.test/confirm-email?code=abc", line);
    }
}

public class EmailOptionsTests
{
    [Theory]
    [InlineData("Log", EmailProvider.Log)]
    [InlineData("log", EmailProvider.Log)]
    [InlineData("SendGrid", EmailProvider.SendGrid)]
    [InlineData("sendgrid", EmailProvider.SendGrid)]
    [InlineData("SENDGRID", EmailProvider.SendGrid)]
    public void A_known_provider_resolves_whatever_the_casing(string configured, EmailProvider expected)
    {
        Assert.Equal(expected, new EmailOptions { Provider = configured }.ResolveProvider());
    }

    [Theory]
    [InlineData("Sendgrd")]
    [InlineData("Mailgun")]
    [InlineData("")]
    public void An_unknown_provider_throws_rather_than_falling_back_to_the_logger(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new EmailOptions { Provider = configured }.ResolveProvider());

        // A typo here used to mean silently sending nothing, which with confirmation required
        // means nobody can finish registering and there is no clue anywhere.
        Assert.Contains("not a provider this app knows about", ex.Message);
        Assert.Contains("Log, SendGrid", ex.Message);
    }
}
