using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Services;
using SocialShare.Web.Infrastructure;

namespace SocialShare.Web.Pages.Admin;

/// <summary>
/// A read only operator view. It deliberately ignores the tenant query filters, which is the
/// only place in the app that does, and it is behind the admin policy.
/// </summary>
public class IndexModel(
    SocialShareDbContext db,
    IAppEmailSender email,
    IOptions<EmailOptions> emailOptions,
    CurrentUser currentUser,
    ILogger<IndexModel> logger) : PageModel
{
    public EmailOptions Email => emailOptions.Value;

    /// <summary>The signed in admin's own address, which is where a test email goes.</summary>
    public string? MyEmail { get; private set; }

    public string? EmailResult { get; private set; }

    public bool EmailResultOk { get; private set; }

    public sealed record Row(
        Guid UserId,
        string Email,
        string DisplayName,
        string TimeZoneId,
        DateTimeOffset CreatedUtc,
        string OrganizationName,
        Plan Plan,
        int ConnectedAccounts,
        int Posts,
        int ScheduledPosts);

    public List<Row> Rows { get; private set; } = [];

    public int TotalOrganizations { get; private set; }

    /// <summary>
    /// Sends a real message to the signed in admin, so whether account email works is something
    /// you can find out on purpose rather than by watching somebody fail to register.
    /// </summary>
    public async Task<IActionResult> OnPostTestEmailAsync(CancellationToken cancellationToken)
    {
        await LoadMyEmailAsync(cancellationToken);

        if (MyEmail is null)
        {
            EmailResult = "Your account has no email address on it, so there is nowhere to send a test.";
            return this.PartialWithViewData("_EmailCheck");
        }

        var sentUtc = DateTimeOffset.UtcNow;
        var message = new EmailMessage(
            To: MyEmail,
            Subject: "SocialShare test email",
            HtmlBody: $"<p>This is a test from SocialShare, sent at {sentUtc:u}.</p>"
                      + "<p>If you are reading it, account email works and new accounts can confirm themselves.</p>",
            TextBody: $"""
                       This is a test from SocialShare, sent at {sentUtc:u}.

                       If you are reading it, account email works and new accounts can confirm themselves.
                       """);

        try
        {
            await email.SendAsync(message, cancellationToken);

            EmailResultOk = true;
            EmailResult = Email.ResolveProvider() == EmailProvider.SendGrid
                ? Email.SandboxMode
                    ? $"SendGrid accepted the message for {MyEmail}, but Email:SandboxMode is on so nothing was delivered. "
                      + "That still proves the API key and the sender identity are good."
                    : $"SendGrid accepted the message. It should arrive at {MyEmail} shortly. Check spam if it does not."
                : $"Email:Provider is Log, so nothing was sent. The message was written to the server log instead.";
        }
        catch (EmailSendException ex)
        {
            logger.LogError(ex, "The admin email test failed.");
            EmailResultOk = false;
            EmailResult = ex.Message;
        }

        return this.PartialWithViewData("_EmailCheck");
    }

    private async Task LoadMyEmailAsync(CancellationToken cancellationToken) =>
        MyEmail = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.Id == currentUser.RequireUserId())
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadMyEmailAsync(cancellationToken);

        var users = await db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Organization)
            .OrderBy(u => u.CreatedUtc)
            .ToListAsync(cancellationToken);

        var accountCounts = await db.SocialAccounts
            .IgnoreQueryFilters()
            .Where(a => a.Status == AccountStatus.Connected)
            .GroupBy(a => a.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, cancellationToken);

        var postCounts = await db.Posts
            .IgnoreQueryFilters()
            .GroupBy(p => p.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Total = g.Count(),
                Scheduled = g.Count(p => p.Status == PostStatus.Scheduled)
            })
            .ToDictionaryAsync(x => x.UserId, cancellationToken);

        TotalOrganizations = await db.Organizations.IgnoreQueryFilters().CountAsync(cancellationToken);

        Rows = users.Select(u =>
        {
            var posts = postCounts.GetValueOrDefault(u.Id);
            return new Row(
                u.Id,
                u.Email ?? string.Empty,
                u.DisplayName,
                u.TimeZoneId,
                u.CreatedUtc,
                u.Organization?.Name ?? string.Empty,
                u.Organization?.Plan ?? Plan.Free,
                accountCounts.GetValueOrDefault(u.Id),
                posts?.Total ?? 0,
                posts?.Scheduled ?? 0);
        }).ToList();
    }
}
