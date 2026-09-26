using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Web.Infrastructure;

namespace SocialShare.Web.Pages.App.Accounts;

/// <summary>
/// Where every OAuth platform sends the browser back to. One page for all of them, keyed by the
/// platform in the route, so there is one redirect URI shape to register.
/// </summary>
public class CallbackModel(
    SocialShareDbContext db,
    AccountConnectionService connections,
    OAuthStateStore oauthState,
    CurrentUser currentUser,
    ILogger<CallbackModel> logger) : PageModel
{
    public bool Succeeded { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(
        SocialPlatform platform,
        string? code,
        string? state,
        string? error,
        string? error_description,
        CancellationToken cancellationToken)
    {
        var handoff = oauthState.Read();
        oauthState.Clear();

        if (error is not null)
        {
            Message = $"{platform} sent back an error: {error}. {error_description}".Trim();
            return Page();
        }

        if (handoff is null || handoff.Platform != platform)
        {
            Message = "That authorization took too long or was started somewhere else. Start the connect again.";
            return Page();
        }

        if (string.IsNullOrEmpty(state) || !CryptographicEquals(state, handoff.State))
        {
            logger.LogWarning("Rejected a {Platform} callback with a mismatched state value.", platform);
            Message = "The authorization state did not match. Start the connect again.";
            return Page();
        }

        if (string.IsNullOrEmpty(code))
        {
            Message = "That callback arrived without an authorization code.";
            return Page();
        }

        var userId = currentUser.RequireUserId();
        var account = await db.SocialAccounts
            .FirstOrDefaultAsync(a => a.UserId == userId && a.Platform == platform, cancellationToken);

        if (account is null)
        {
            Message = "That platform is not set up on this account any more.";
            return Page();
        }

        var redirectUri = $"{Request.Scheme}://{Request.Host}{Request.Path}";
        Succeeded = await connections.CompleteOAuthAsync(
            account, redirectUri, code, handoff.CodeVerifier, cancellationToken);

        Message = Succeeded
            ? $"{platform} is connected{(account.DisplayName is null ? "" : $" as {account.DisplayName}")}."
            : account.LastError ?? "The platform refused to finish the connection.";

        return Page();
    }

    /// <summary>Constant time compare so the state check cannot be probed a character at a time.</summary>
    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
