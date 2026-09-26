using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Core.Services;
using SocialShare.Web.Infrastructure;

namespace SocialShare.Web.Pages.App.Posts;

public class DetailsModel(
    SocialShareDbContext db,
    ISocialPlatformRegistry registry,
    PublishingService publisher,
    CurrentUser currentUser) : PageModel
{
    public Post Post { get; private set; } = null!;

    public string TimeZoneId { get; private set; } = AppTimeZone.Default;

    public List<PublishLog> Logs { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public PlatformCapabilities Capabilities(SocialPlatform platform) => registry.Get(platform).Capabilities;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, cancellationToken);
        return loaded ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostRetryAsync(Guid id, Guid targetId, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        var target = Post.Targets.FirstOrDefault(t => t.Id == targetId);
        if (target is null)
        {
            return NotFound();
        }

        // Retrying by hand resets the automatic attempt count, so the three tries start over.
        target.Attempts = 0;
        target.NextAttemptUtc = null;
        await db.SaveChangesAsync(cancellationToken);

        await publisher.RetryTargetAsync(targetId, cancellationToken);
        await LoadAsync(id, cancellationToken);

        return Request.IsHtmx() ? Partial("_PostResultsPanel", this) : RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostPublishAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        if (!Post.Targets.Any(t => t.Enabled))
        {
            ErrorMessage = "Nothing is enabled on this post.";
            return RedirectToPage(new { id });
        }

        if (await publisher.TryClaimAsync(id, [PostStatus.Draft, PostStatus.Scheduled], cancellationToken))
        {
            await publisher.PublishClaimedPostAsync(id, cancellationToken);
            StatusMessage = "Published. Per platform results are below.";
        }
        else
        {
            ErrorMessage = "That post is already publishing or has already gone out.";
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        if (Post.Status != PostStatus.Scheduled)
        {
            ErrorMessage = "Only a scheduled post can be canceled.";
            return RedirectToPage(new { id });
        }

        Post.Status = PostStatus.Canceled;
        Post.ScheduledUtc = null;
        Post.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        StatusMessage = "Canceled. It will not go out.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        db.Posts.Remove(Post);
        await db.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = "Deleted. Anything already published stays published.";
        return Redirect("/app/posts");
    }

    /// <summary>Inline confirm panel, so no browser dialog is needed for a destructive action.</summary>
    public async Task<IActionResult> OnGetConfirmAsync(Guid id, string action, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        ViewData["ConfirmAction"] = action;
        return Partial("_PostConfirm", this);
    }

    public async Task<IActionResult> OnGetActionsAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        return Partial("_PostActions", this);
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await db.LoadWithOrganizationAsync(currentUser.RequireUserId(), cancellationToken);
        TimeZoneId = user?.TimeZoneId ?? AppTimeZone.Default;

        var post = await db.Posts
            .Include(p => p.Targets)
            .ThenInclude(t => t.Image)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (post is null)
        {
            return false;
        }

        Post = post;

        Logs = await db.PublishLogs
            .Where(l => l.PostId == id)
            .OrderByDescending(l => l.StartedUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        return true;
    }
}
