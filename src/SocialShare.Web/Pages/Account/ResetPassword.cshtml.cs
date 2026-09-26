using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using SocialShare.Core.Domain;

namespace SocialShare.Web.Pages.Account;

public class ResetPasswordModel(UserManager<ApplicationUser> userManager) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required]
        [EmailAddress(ErrorMessage = "That does not look like an email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "A password is required.")]
        [StringLength(200, MinimumLength = 10, ErrorMessage = "The password needs at least 10 characters.")]
        public string Password { get; set; } = string.Empty;

        [Required]
        [Compare(nameof(Password), ErrorMessage = "The two passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;
    }

    public IActionResult OnGet(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return BadRequest("That reset link is missing its code.");
        }

        Input.Code = code;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await userManager.FindByEmailAsync(Input.Email);
        if (user is null)
        {
            // Same message either way so this cannot be used to enumerate accounts.
            return RedirectToPage("/Account/Login", new { });
        }

        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Input.Code));
        }
        catch (FormatException)
        {
            ModelState.AddModelError(string.Empty, "That reset link is malformed. Ask for a new one.");
            return Page();
        }

        var result = await userManager.ResetPasswordAsync(user, token, Input.Password);
        if (result.Succeeded)
        {
            TempData["StatusMessage"] = "Your password is set. Sign in with it.";
            return Redirect("/login");
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }

        return Page();
    }
}
