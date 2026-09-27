using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SocialShare.Web.Pages.Account;

public class ConfirmEmailSentModel : PageModel
{
    public string Email { get; private set; } = string.Empty;

    /// <summary>False when the provider refused the message, so the page can be honest.</summary>
    public bool Sent { get; private set; } = true;

    public void OnGet(string? email, bool sent = true)
    {
        Email = email ?? "your inbox";
        Sent = sent;
    }
}
