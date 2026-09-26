using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Services;
using SocialShare.Web.Infrastructure;

namespace SocialShare.Web.Pages.App.Settings;

public class IndexModel(
    SocialShareDbContext db,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    CurrentUser currentUser) : PageModel
{
    [BindProperty]
    public ProfileInput Profile { get; set; } = new();

    [BindProperty]
    public PasswordInput Password { get; set; } = new();

    public string Email { get; private set; } = string.Empty;
    public string OrganizationName { get; private set; } = string.Empty;
    public Plan Plan { get; private set; } = Plan.Free;
    public int? ScheduledPostLimit => FeatureGate.ScheduledPostLimit(Plan);

    [TempData]
    public string? StatusMessage { get; set; }

    public class ProfileInput
    {
        [Required(ErrorMessage = "A display name is required.")]
        [StringLength(200)]
        public string DisplayName { get; set; } = string.Empty;

        [Required]
        public string TimeZoneId { get; set; } = AppTimeZone.Default;
    }

    public class PasswordInput
    {
        [Required(ErrorMessage = "Your current password is required.")]
        public string Current { get; set; } = string.Empty;

        [Required(ErrorMessage = "A new password is required.")]
        [StringLength(200, MinimumLength = 10, ErrorMessage = "The password needs at least 10 characters.")]
        public string New { get; set; } = string.Empty;

        [Required]
        [Compare(nameof(New), ErrorMessage = "The two passwords do not match.")]
        public string Confirm { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await LoadAsync(cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        Profile.DisplayName = user.DisplayName;
        Profile.TimeZoneId = user.TimeZoneId;
        return Page();
    }

    public async Task<IActionResult> OnPostProfileAsync(CancellationToken cancellationToken)
    {
        var user = await LoadAsync(cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        ModelState.Remove("Password.Current");
        ModelState.Remove("Password.New");
        ModelState.Remove("Password.Confirm");

        if (!AppTimeZone.CommonZones.Contains(Profile.TimeZoneId))
        {
            ModelState.AddModelError("Profile.TimeZoneId", "Pick a time zone from the list.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        user.DisplayName = Profile.DisplayName.Trim();
        user.TimeZoneId = Profile.TimeZoneId;
        await db.SaveChangesAsync(cancellationToken);

        StatusMessage = "Profile saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync(CancellationToken cancellationToken)
    {
        var user = await LoadAsync(cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        ModelState.Remove("Profile.DisplayName");
        ModelState.Remove("Profile.TimeZoneId");
        Profile.DisplayName = user.DisplayName;
        Profile.TimeZoneId = user.TimeZoneId;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await userManager.ChangePasswordAsync(user, Password.Current, Password.New);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        // Keeps the current browser signed in with a fresh security stamp.
        await signInManager.RefreshSignInAsync(user);

        StatusMessage = "Password changed.";
        return RedirectToPage();
    }

    private async Task<ApplicationUser?> LoadAsync(CancellationToken cancellationToken)
    {
        var user = await db.LoadWithOrganizationAsync(currentUser.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return null;
        }

        Email = user.Email ?? string.Empty;
        OrganizationName = user.Organization?.Name ?? string.Empty;
        Plan = user.Organization?.Plan ?? Plan.Free;
        return user;
    }
}
