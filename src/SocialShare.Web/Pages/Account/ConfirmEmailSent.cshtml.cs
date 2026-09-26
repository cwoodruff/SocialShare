using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SocialShare.Web.Pages.Account;

public class ConfirmEmailSentModel : PageModel
{
    public string Email { get; private set; } = string.Empty;

    public void OnGet(string? email) => Email = email ?? "your inbox";
}
