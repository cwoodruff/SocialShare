using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Services;

namespace SocialShare.Web.Pages.Account;

public class RegisterModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    SocialShareDbContext db,
    IAppEmailSender email,
    IOptions<AppOptions> appOptions,
    ILogger<RegisterModel> logger) : PageModel
{
    private readonly AppOptions _app = appOptions.Value;

    public bool RegistrationEnabled => _app.RegistrationEnabled;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "Tell me what to call you.")]
        [StringLength(200)]
        public string DisplayName { get; set; } = string.Empty;

        [Required(ErrorMessage = "An email address is required.")]
        [EmailAddress(ErrorMessage = "That does not look like an email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "A password is required.")]
        [StringLength(200, MinimumLength = 10, ErrorMessage = "The password needs at least 10 characters.")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Confirm the password.")]
        [Compare(nameof(Password), ErrorMessage = "The two passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;

        public string TimeZoneId { get; set; } = AppTimeZone.Default;
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!RegistrationEnabled)
        {
            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!AppTimeZone.CommonZones.Contains(Input.TimeZoneId))
        {
            Input.TimeZoneId = AppTimeZone.Default;
        }

        // Every user gets their own organization. Team features are not built, but the schema
        // and every owned row already carry the tenant so adding them later is additive.
        var organization = new Organization
        {
            Name = string.IsNullOrWhiteSpace(Input.DisplayName) ? Input.Email : Input.DisplayName,
            Plan = Plan.Free
        };

        db.Organizations.Add(organization);
        await db.SaveChangesAsync(cancellationToken);

        var user = new ApplicationUser
        {
            UserName = Input.Email,
            Email = Input.Email,
            DisplayName = Input.DisplayName.Trim(),
            TimeZoneId = Input.TimeZoneId,
            OrganizationId = organization.Id
        };

        var created = await userManager.CreateAsync(user, Input.Password);
        if (!created.Succeeded)
        {
            db.Organizations.Remove(organization);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var error in created.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        logger.LogInformation("Registered {Email}.", Input.Email);

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = Url.Page("/Account/ConfirmEmail", null,
            new { userId = user.Id, code = encoded }, Request.Scheme)!;

        await email.SendAsync(
            Input.Email,
            "Confirm your SocialShare account",
            $"""<p>Confirm your account by <a href="{HtmlEncoder.Default.Encode(link)}">clicking here</a>.</p>""",
            cancellationToken);

        if (_app.RequireConfirmedAccount)
        {
            return RedirectToPage("/Account/ConfirmEmailSent", new { email = Input.Email });
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return Redirect("/app");
    }
}
