using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using SocialShare.Core.Domain;
using SocialShare.Core.Services;

namespace SocialShare.Web.Pages.Account;

public class ForgotPasswordModel(
    UserManager<ApplicationUser> userManager,
    IAppEmailSender email,
    ILogger<ForgotPasswordModel> logger) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "An email address is required.")]
    [EmailAddress(ErrorMessage = "That does not look like an email address.")]
    public string Email { get; set; } = string.Empty;

    public bool Sent { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await userManager.FindByEmailAsync(Email);

        // Always report the same thing so this page cannot be used to test which emails exist.
        Sent = true;

        if (user is null)
        {
            return Page();
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = Url.Page("/Account/ResetPassword", null, new { code = encoded }, Request.Scheme)!;

        var message = new EmailMessage(
            To: Email,
            Subject: "Reset your SocialShare password",
            HtmlBody: $"""
                       <p>Reset your SocialShare password by <a href="{HtmlEncoder.Default.Encode(link)}">clicking here</a>.</p>
                       <p>If the link does not work, paste this into your browser:</p>
                       <p>{HtmlEncoder.Default.Encode(link)}</p>
                       <p>If you did not ask for this, ignore the message. Your password has not changed.</p>
                       """,
            TextBody: $"""
                       Reset your SocialShare password by opening this link:

                       {link}

                       If you did not ask for this, ignore the message. Your password has not changed.
                       """);

        try
        {
            await email.SendAsync(message, cancellationToken);
        }
        catch (EmailSendException ex)
        {
            // Same reasoning as the resend page: reporting the failure only when the address
            // exists would turn this into a way to discover which addresses have accounts.
            // The operator finds out from the log.
            logger.LogError(ex, "Could not send a password reset link to {Email}.", Email);
        }

        return Page();
    }
}
