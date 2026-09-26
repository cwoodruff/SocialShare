using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SocialShare.Core.Domain;
using SocialShare.Web.Infrastructure;

namespace SocialShare.Web.Pages.Account;

/// <summary>
/// The way back in for somebody whose confirmation email went missing. Without this, turning on
/// RequireConfirmedAccount would strand anyone who lost the original link, because registration
/// is the only other place that sends one and it refuses an email that already exists.
/// </summary>
public class ResendConfirmationModel(
    UserManager<ApplicationUser> userManager,
    ConfirmationEmail confirmationEmail) : PageModel
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

        // Always report the same thing, so this page cannot be used to work out which addresses
        // have accounts or which of them are already confirmed.
        Sent = true;

        var user = await userManager.FindByEmailAsync(Email);
        if (user is null || await userManager.IsEmailConfirmedAsync(user))
        {
            return Page();
        }

        await confirmationEmail.SendAsync(user, this, cancellationToken);
        return Page();
    }
}
