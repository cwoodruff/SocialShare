using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using SocialShare.Core.Domain;

namespace SocialShare.Web.Pages.Account;

public class ConfirmEmailModel(UserManager<ApplicationUser> userManager) : PageModel
{
    public bool Succeeded { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public async Task OnGetAsync(Guid? userId, string? code)
    {
        if (userId is null || string.IsNullOrEmpty(code))
        {
            Message = "That confirmation link is incomplete.";
            return;
        }

        var user = await userManager.FindByIdAsync(userId.Value.ToString());
        if (user is null)
        {
            Message = "That confirmation link does not match an account.";
            return;
        }

        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        }
        catch (FormatException)
        {
            Message = "That confirmation link is malformed.";
            return;
        }

        var result = await userManager.ConfirmEmailAsync(user, token);
        Succeeded = result.Succeeded;
        Message = result.Succeeded
            ? "Your email is confirmed."
            : "That confirmation link is expired or has already been used.";
    }
}
