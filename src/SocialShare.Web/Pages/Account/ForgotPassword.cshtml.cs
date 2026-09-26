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
    IAppEmailSender email) : PageModel
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

        await email.SendAsync(
            Email,
            "Reset your SocialShare password",
            $"""<p>Reset your password by <a href="{HtmlEncoder.Default.Encode(link)}">clicking here</a>. Ignore this if you did not ask for it.</p>""",
            cancellationToken);

        return Page();
    }
}
