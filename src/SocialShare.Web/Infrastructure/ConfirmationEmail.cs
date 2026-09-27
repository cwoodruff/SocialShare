using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using SocialShare.Core.Domain;
using SocialShare.Core.Services;

namespace SocialShare.Web.Infrastructure;

/// <summary>
/// Builds and sends the confirm your email message. Registration and the resend page both need
/// exactly this, and a confirmation link that differs between the two would be a fun bug.
/// </summary>
public sealed class ConfirmationEmail(
    UserManager<ApplicationUser> userManager,
    IAppEmailSender email,
    ILogger<ConfirmationEmail> logger)
{
    /// <summary>
    /// Returns false when the provider refused the message. The caller decides what to say
    /// about that: the account already exists either way, so throwing would leave the person
    /// looking at a 500 with an account they cannot get into.
    /// </summary>
    public async Task<bool> TrySendAsync(
        ApplicationUser user,
        PageModel page,
        CancellationToken cancellationToken)
    {
        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

        var link = page.Url.Page(
            "/Account/ConfirmEmail",
            pageHandler: null,
            values: new { userId = user.Id, code = encoded },
            protocol: page.Request.Scheme)!;

        var message = new EmailMessage(
            To: user.Email!,
            Subject: "Confirm your SocialShare account",
            HtmlBody: $"""
                       <p>Confirm your SocialShare account by <a href="{HtmlEncoder.Default.Encode(link)}">clicking here</a>.</p>
                       <p>If the link does not work, paste this into your browser:</p>
                       <p>{HtmlEncoder.Default.Encode(link)}</p>
                       <p>If you did not create this account, ignore this message.</p>
                       """,
            TextBody: $"""
                       Confirm your SocialShare account by opening this link:

                       {link}

                       If you did not create this account, ignore this message.
                       """);

        try
        {
            await email.SendAsync(message, cancellationToken);
            logger.LogInformation("Sent a confirmation link to {Email}.", user.Email);
            return true;
        }
        catch (EmailSendException ex)
        {
            // Loud, because with confirmation required this is the difference between an
            // account that works and one that can never sign in.
            logger.LogError(ex, "Could not send the confirmation link to {Email}.", user.Email);
            return false;
        }
    }
}
