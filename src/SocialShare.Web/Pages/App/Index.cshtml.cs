using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Core.Services;
using SocialShare.Web.Infrastructure;

namespace SocialShare.Web.Pages.App;

public class IndexModel(
    SocialShareDbContext db,
    ISocialPlatformRegistry registry,
    CurrentUser currentUser) : PageModel
{
    public string DisplayName { get; private set; } = string.Empty;
    public string TimeZoneId { get; private set; } = AppTimeZone.Default;

    public List<Post> Upcoming { get; private set; } = [];
    public List<Post> Recent { get; private set; } = [];

    public int ConnectedCount { get; private set; }
    public int PlatformCount => registry.All.Count;
    public List<string> NeedsAttention { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var user = await db.LoadWithOrganizationAsync(userId, cancellationToken);
        DisplayName = user?.DisplayName ?? "there";
        TimeZoneId = user?.TimeZoneId ?? AppTimeZone.Default;

        Upcoming = await db.Posts
            .Include(p => p.Targets)
            .Where(p => p.Status == PostStatus.Scheduled)
            .OrderBy(p => p.ScheduledUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        Recent = await db.Posts
            .Include(p => p.Targets)
            .Where(p => p.Status != PostStatus.Scheduled && p.Status != PostStatus.Draft)
            .OrderByDescending(p => p.PublishedUtc ?? p.UpdatedUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        var accounts = await db.SocialAccounts.ToListAsync(cancellationToken);
        ConnectedCount = accounts.Count(a => a.Status == AccountStatus.Connected);

        NeedsAttention = accounts
            .Where(a => a.Status == AccountStatus.Error)
            .Select(a => registry.Get(a.Platform).Capabilities.DisplayName)
            .ToList();
    }
}
