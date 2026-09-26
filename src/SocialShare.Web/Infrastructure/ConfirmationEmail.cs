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
    public async Task SendAsync(
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

        await email.SendAsync(
            user.Email!,
            "Confirm your SocialShare account",
            $"""
             <p>Confirm your SocialShare account by <a href="{HtmlEncoder.Default.Encode(link)}">clicking here</a>.</p>
             <p>If you did not create this account, ignore this message.</p>
             """,
            cancellationToken);

        logger.LogInformation("Sent a confirmation link to {Email}.", user.Email);
    }
}
