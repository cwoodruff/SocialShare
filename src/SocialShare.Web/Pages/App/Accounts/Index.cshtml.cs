using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Web.Infrastructure;

namespace SocialShare.Web.Pages.App.Accounts;

public class IndexModel(
    SocialShareDbContext db,
    ISocialPlatformRegistry registry,
    AccountConnectionService connections,
    OAuthStateStore oauthState,
    CurrentUser currentUser,
    Microsoft.Extensions.Options.IOptions<SocialShare.Core.Services.AppOptions> appOptions) : PageModel
{
    /// <summary>Everything one platform card needs to render itself.</summary>
    public sealed record Card(
        PlatformCapabilities Capabilities,
        SocialAccount? Account,
        IReadOnlyDictionary<string, string> Credentials,
        bool IsConfigured)
    {
        public bool IsConnected => Account?.Status == AccountStatus.Connected;
    }

    public List<Card> Cards { get; private set; } = [];

    public Card? Single { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    /// <summary>Links every credential form at the setup doc for that platform.</summary>
    public string DocsUrl(string? anchor)
    {
        var baseUrl = (appOptions.Value.DocsBaseUrl ?? string.Empty).TrimEnd('/');
        var url = $"{baseUrl}/platform-setup.md";
        return anchor is null ? url : $"{url}#{anchor}";
    }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Cards = await BuildCardsAsync(cancellationToken);

    /// <summary>Saves the credential form for one platform and swaps that card back.</summary>
    public async Task<IActionResult> OnPostSaveAsync(SocialPlatform platform, CancellationToken cancellationToken)
    {
        var account = await LoadOrCreateAsync(platform, cancellationToken);
        var platformImpl = registry.Get(platform);

        var submitted = new Dictionary<string, string>();
        foreach (var field in platformImpl.Capabilities.CredentialFields)
        {
            if (Request.Form.TryGetValue(field.Key, out var value))
            {
                submitted[field.Key] = value.ToString();
            }
        }

        await connections.SaveCredentialsAsync(account, submitted, cancellationToken);

        return await CardResultAsync(platform, "Saved.", null, cancellationToken);
    }

    /// <summary>
    /// Starts a connection. Direct credential platforms connect in place and swap the card back.
    /// OAuth platforms need the browser to leave, so htmx is told to redirect.
    /// </summary>
    public async Task<IActionResult> OnPostConnectAsync(SocialPlatform platform, CancellationToken cancellationToken)
    {
        var account = await LoadOrCreateAsync(platform, cancellationToken);
        var redirectUri = BuildRedirectUri(platform);

        var start = await connections.StartConnectAsync(account, redirectUri, cancellationToken);

        if (start.AuthorizationUrl is null)
        {
            var error = start.Error ?? account.LastError;
            return await CardResultAsync(platform, error is null ? "Connected." : null, error, cancellationToken);
        }

        // The verifier and state have to survive the round trip to the platform and back.
        oauthState.Save(platform, start.State!, start.CodeVerifier);

        if (Request.IsHtmx())
        {
            Response.HtmxRedirect(start.AuthorizationUrl.ToString());
            return new EmptyResult();
        }

        return Redirect(start.AuthorizationUrl.ToString());
    }

    public async Task<IActionResult> OnPostTestAsync(SocialPlatform platform, CancellationToken cancellationToken)
    {
        var account = await FindAsync(platform, cancellationToken);
        if (account is null)
        {
            return await CardResultAsync(platform, null, "That platform is not set up yet.", cancellationToken);
        }

        var result = await connections.TestAsync(account, cancellationToken);
        return await CardResultAsync(
            platform,
            result.Success ? result.Message : null,
            result.Success ? null : result.Message,
            cancellationToken);
    }

    public async Task<IActionResult> OnPostDisconnectAsync(SocialPlatform platform, CancellationToken cancellationToken)
    {
        var account = await FindAsync(platform, cancellationToken);
        if (account is not null)
        {
            await connections.DisconnectAsync(account, cancellationToken);
        }

        return await CardResultAsync(platform, "Disconnected. The saved fields are still here.", null, cancellationToken);
    }

    public async Task<IActionResult> OnPostForgetAsync(SocialPlatform platform, CancellationToken cancellationToken)
    {
        var account = await FindAsync(platform, cancellationToken);
        if (account is not null)
        {
            await connections.ForgetAsync(account, cancellationToken);
        }

        return await CardResultAsync(platform, "Forgotten. Everything for that platform is gone.", null, cancellationToken);
    }

    /// <summary>Renders the inline confirm panel without changing anything.</summary>
    public async Task<IActionResult> OnGetConfirmAsync(SocialPlatform platform, string action, CancellationToken cancellationToken)
    {
        var card = await BuildCardAsync(platform, cancellationToken);
        ViewData["ConfirmAction"] = action;
        Single = card;
        return this.PartialWithViewData("_AccountConfirm");
    }

    /// <summary>Re-renders a single card, used to back out of a confirm panel.</summary>
    public Task<IActionResult> OnGetCardAsync(SocialPlatform platform, CancellationToken cancellationToken) =>
        CardResultAsync(platform, null, null, cancellationToken);

    private async Task<IActionResult> CardResultAsync(
        SocialPlatform platform, string? status, string? error, CancellationToken cancellationToken)
    {
        if (!Request.IsHtmx())
        {
            StatusMessage = status;
            ErrorMessage = error;
            return RedirectToPage();
        }

        Single = await BuildCardAsync(platform, cancellationToken);
        ViewData["CardStatus"] = status;
        ViewData["CardError"] = error;
        return this.PartialWithViewData("_AccountCard");
    }

    private string BuildRedirectUri(SocialPlatform platform) =>
        $"{Request.Scheme}://{Request.Host}{Url.Page("/App/Accounts/Callback", new { platform })}";

    private Task<SocialAccount?> FindAsync(SocialPlatform platform, CancellationToken cancellationToken) =>
        db.SocialAccounts.FirstOrDefaultAsync(
            a => a.UserId == currentUser.RequireUserId() && a.Platform == platform, cancellationToken);

    private async Task<SocialAccount> LoadOrCreateAsync(SocialPlatform platform, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var user = await db.LoadWithOrganizationAsync(userId, cancellationToken)
                   ?? throw new InvalidOperationException("The signed in user is missing.");

        return await connections.GetOrCreateAsync(userId, user.OrganizationId, platform, cancellationToken);
    }

    private async Task<List<Card>> BuildCardsAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var accounts = await db.SocialAccounts
            .Where(a => a.UserId == userId)
            .ToDictionaryAsync(a => a.Platform, cancellationToken);

        return registry.All.Select(p => BuildCard(p, accounts.GetValueOrDefault(p.Platform))).ToList();
    }

    private async Task<Card> BuildCardAsync(SocialPlatform platform, CancellationToken cancellationToken) =>
        BuildCard(registry.Get(platform), await FindAsync(platform, cancellationToken));

    private Card BuildCard(ISocialPlatform platform, SocialAccount? account)
    {
        var credentials = account is null
            ? new Dictionary<string, string>()
            : connections.ReadCredentials(account);

        return new Card(
            platform.Capabilities,
            account,
            credentials,
            AccountConnectionService.IsConfigured(platform, credentials));
    }
}
