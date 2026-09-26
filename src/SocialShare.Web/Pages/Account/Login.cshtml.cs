using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using SocialShare.Core.Domain;
using SocialShare.Core.Services;

namespace SocialShare.Web.Pages.Account;

public class LoginModel(
    SignInManager<ApplicationUser> signInManager,
    IOptions<AppOptions> appOptions) : PageModel
{
    public bool RegistrationEnabled => appOptions.Value.RegistrationEnabled;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "An email address is required.")]
        [EmailAddress(ErrorMessage = "That does not look like an email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "A password is required.")]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; } = true;
    }

    public async Task OnGetAsync()
    {
        // Clear any half finished external cookie so a failed attempt does not stick around.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signInManager.PasswordSignInAsync(
            Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/app" : returnUrl);
        }

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "That account is locked out. Try again in a few minutes.");
            return Page();
        }

        if (result.IsNotAllowed)
        {
            ModelState.AddModelError(string.Empty,
                "That account has not confirmed its email address yet. Check your inbox, or the server log in development.");
            return Page();
        }

        ModelState.AddModelError(string.Empty, "That email and password do not match an account.");
        return Page();
    }
}
