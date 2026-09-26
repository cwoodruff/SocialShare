using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;

namespace SocialShare.Web.Pages.Admin;

/// <summary>
/// A read only operator view. It deliberately ignores the tenant query filters, which is the
/// only place in the app that does, and it is behind the admin policy.
/// </summary>
public class IndexModel(SocialShareDbContext db) : PageModel
{
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

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
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
