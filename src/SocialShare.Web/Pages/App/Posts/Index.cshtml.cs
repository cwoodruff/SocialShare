using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Core.Services;
using SocialShare.Web.Infrastructure;

namespace SocialShare.Web.Pages.App.Posts;

public class IndexModel(
    SocialShareDbContext db,
    ISocialPlatformRegistry registry,
    CurrentUser currentUser) : PageModel
{
    public const int PageSize = 25;

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public PostStatus? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public SocialPlatform? Platform { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public List<Post> Results { get; private set; } = [];
    public int TotalCount { get; private set; }
    public string TimeZoneId { get; private set; } = AppTimeZone.Default;

    public IReadOnlyList<PlatformCapabilities> Platforms => registry.All.Select(p => p.Capabilities).ToList();

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        // The filter form posts with htmx and only the results table is swapped back.
        return Request.IsHtmx() ? this.PartialWithViewData("_PostResults") : Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var user = await db.LoadWithOrganizationAsync(currentUser.RequireUserId(), cancellationToken);
        TimeZoneId = user?.TimeZoneId ?? AppTimeZone.Default;

        var query = db.Posts.Include(p => p.Targets).AsQueryable();

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            query = query.Where(p =>
                EF.Functions.Like(p.Title, $"%{term}%")
                || p.Targets.Any(t => EF.Functions.Like(t.Body, $"%{term}%")));
        }

        if (Status is { } status)
        {
            query = query.Where(p => p.Status == status);
        }

        if (Platform is { } platform)
        {
            query = query.Where(p => p.Targets.Any(t => t.Platform == platform && t.Enabled));
        }

        if (From is { } from)
        {
            var fromUtc = AppTimeZone.ToUtc(from.ToDateTime(TimeOnly.MinValue), TimeZoneId);
            query = query.Where(p => (p.ScheduledUtc ?? p.PublishedUtc ?? p.CreatedUtc) >= fromUtc);
        }

        if (To is { } to)
        {
            var toUtc = AppTimeZone.ToUtc(to.ToDateTime(TimeOnly.MaxValue), TimeZoneId);
            query = query.Where(p => (p.ScheduledUtc ?? p.PublishedUtc ?? p.CreatedUtc) <= toUtc);
        }

        TotalCount = await query.CountAsync(cancellationToken);

        PageNumber = Math.Max(1, PageNumber);

        Results = await query
            .OrderByDescending(p => p.ScheduledUtc ?? p.PublishedUtc ?? p.CreatedUtc)
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(cancellationToken);
    }

    public string PageLink(int page)
    {
        var parts = new List<string> { $"pageNumber={page}" };

        if (!string.IsNullOrWhiteSpace(Search)) { parts.Add($"search={Uri.EscapeDataString(Search)}"); }
        if (Status is { } s) { parts.Add($"status={s}"); }
        if (Platform is { } p) { parts.Add($"platform={p}"); }
        if (From is { } f) { parts.Add($"from={f:yyyy-MM-dd}"); }
        if (To is { } t) { parts.Add($"to={t:yyyy-MM-dd}"); }

        return "/app/posts?" + string.Join('&', parts);
    }
}
