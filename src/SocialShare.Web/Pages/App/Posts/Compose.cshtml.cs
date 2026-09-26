using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Core.Services;
using SocialShare.Core.Text;
using SocialShare.Web.Infrastructure;

namespace SocialShare.Web.Pages.App.Posts;

public class ComposeModel(
    SocialShareDbContext db,
    ISocialPlatformRegistry registry,
    ImageService images,
    PublishingService publisher,
    CurrentUser currentUser,
    ILogger<ComposeModel> logger) : PageModel
{
    public sealed class TargetInput
    {
        public SocialPlatform Platform { get; set; }
        public bool Enabled { get; set; }
        public string? Body { get; set; }
        public Guid? ImageId { get; set; }

        [StringLength(1000)]
        public string? ImageAltText { get; set; }
    }

    /// <summary>Everything the view needs about one platform panel.</summary>
    public sealed record Panel(
        int Index,
        PlatformCapabilities Capabilities,
        TargetInput Input,
        StoredImage? Image,
        int CharacterLimit,
        bool AccountConnected,
        PostTarget? Result);

    [BindProperty]
    [Required(ErrorMessage = "Give the post a title so you can find it later.")]
    [StringLength(300)]
    public string Title { get; set; } = string.Empty;

    [BindProperty]
    public string? MasterBody { get; set; }

    [BindProperty]
    public List<TargetInput> Targets { get; set; } = [];

    /// <summary>Wall clock value in the user's zone, as typed into the datetime-local control.</summary>
    [BindProperty]
    public DateTime? ScheduledLocal { get; set; }

    public Guid? PostId { get; private set; }

    public PostStatus Status { get; private set; } = PostStatus.Draft;

    public string TimeZoneId { get; private set; } = AppTimeZone.Default;

    public List<Panel> Panels { get; private set; } = [];

    public string? ErrorMessage { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsEdit => PostId is not null;

    public async Task<IActionResult> OnGetAsync(Guid? id, Guid? duplicateOf, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var user = await db.LoadWithOrganizationAsync(userId, cancellationToken);
        TimeZoneId = user?.TimeZoneId ?? AppTimeZone.Default;

        var source = id ?? duplicateOf;
        Post? post = null;

        if (source is not null)
        {
            post = await db.Posts
                .Include(p => p.Targets)
                .ThenInclude(t => t.Image)
                .FirstOrDefaultAsync(p => p.Id == source, cancellationToken);

            if (post is null)
            {
                return NotFound();
            }
        }

        if (id is not null && post is not null)
        {
            if (post.Status is PostStatus.Publishing)
            {
                StatusMessage = "That post is publishing right now, so it cannot be edited.";
                return Redirect($"/app/posts/{post.Id}");
            }

            PostId = post.Id;
            Status = post.Status;
            Title = post.Title;
            MasterBody = post.MasterBody;
            ScheduledLocal = post.ScheduledUtc is { } utc ? AppTimeZone.ToLocal(utc, TimeZoneId) : null;
        }
        else if (post is not null)
        {
            Title = post.Title + " (copy)";
            MasterBody = post.MasterBody;
        }

        Targets = registry.All.Select(p =>
        {
            var existing = post?.Targets.FirstOrDefault(t => t.Platform == p.Platform);
            return new TargetInput
            {
                Platform = p.Platform,
                Enabled = existing?.Enabled ?? false,
                Body = existing?.Body,
                ImageId = existing?.ImageId,
                ImageAltText = existing?.ImageAltText
            };
        }).ToList();

        await BuildPanelsAsync(post, cancellationToken);
        return Page();
    }

    public Task<IActionResult> OnPostSaveAsync(Guid? id, CancellationToken cancellationToken) =>
        SubmitAsync(id, SubmitAction.SaveDraft, cancellationToken);

    public Task<IActionResult> OnPostPublishAsync(Guid? id, CancellationToken cancellationToken) =>
        SubmitAsync(id, SubmitAction.PublishNow, cancellationToken);

    public Task<IActionResult> OnPostScheduleAsync(Guid? id, CancellationToken cancellationToken) =>
        SubmitAsync(id, SubmitAction.Schedule, cancellationToken);

    private enum SubmitAction
    {
        SaveDraft,
        PublishNow,
        Schedule
    }

    private async Task<IActionResult> SubmitAsync(Guid? id, SubmitAction action, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var user = await db.LoadWithOrganizationAsync(userId, cancellationToken)
                   ?? throw new InvalidOperationException("The signed in user is missing.");
        TimeZoneId = user.TimeZoneId;

        var post = id is null
            ? null
            : await db.Posts.Include(p => p.Targets).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (id is not null && post is null)
        {
            return NotFound();
        }

        PostId = post?.Id;
        Status = post?.Status ?? PostStatus.Draft;

        var enabled = Targets.Where(t => t.Enabled).ToList();

        if (action != SubmitAction.SaveDraft && enabled.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Enable at least one platform before publishing.");
        }

        DateTimeOffset? scheduledUtc = null;
        if (action == SubmitAction.Schedule)
        {
            if (ScheduledLocal is null)
            {
                ModelState.AddModelError(string.Empty, "Pick a date and time to schedule for.");
            }
            else
            {
                scheduledUtc = AppTimeZone.ToUtc(ScheduledLocal.Value, TimeZoneId);
                if (scheduledUtc <= DateTimeOffset.UtcNow.AddSeconds(-30))
                {
                    ModelState.AddModelError(string.Empty, "That time has already passed. Pick one in the future.");
                }
            }
        }

        // Per platform validation. Bodies are checked here and again in the publisher, because
        // the limit for one platform is read from that platform and can change under us.
        var connected = await db.SocialAccounts
            .Where(a => a.UserId == userId)
            .ToDictionaryAsync(a => a.Platform, cancellationToken);

        foreach (var target in enabled)
        {
            var capabilities = registry.Get(target.Platform).Capabilities;
            var limit = connected.GetValueOrDefault(target.Platform)?.CharacterLimitOverride
                        ?? capabilities.DefaultCharacterLimit;

            var length = RichText.CountCharacters(target.Body);

            if (length == 0 && target.ImageId is null)
            {
                ModelState.AddModelError(string.Empty,
                    $"{capabilities.DisplayName} is enabled but has nothing in it. Write something or turn it off.");
            }

            if (length > limit)
            {
                ModelState.AddModelError(string.Empty,
                    $"The {capabilities.DisplayName} body is {length} characters and the limit is {limit}.");
            }

            if (capabilities.ImageRequired && target.ImageId is null)
            {
                ModelState.AddModelError(string.Empty,
                    $"{capabilities.DisplayName} will not take a post without an image.");
            }

            if (action != SubmitAction.SaveDraft
                && connected.GetValueOrDefault(target.Platform)?.Status != AccountStatus.Connected)
            {
                ModelState.AddModelError(string.Empty,
                    $"{capabilities.DisplayName} is enabled but not connected. Connect it on the accounts page.");
            }
        }

        if (action == SubmitAction.Schedule && post is null)
        {
            var limit = FeatureGate.ScheduledPostLimit(user.Organization?.Plan ?? Plan.Free);
            if (limit is not null)
            {
                var queued = await db.Posts.CountAsync(p => p.Status == PostStatus.Scheduled, cancellationToken);
                if (queued >= limit)
                {
                    ModelState.AddModelError(string.Empty,
                        $"The free plan holds {limit} scheduled posts at a time. Publish or cancel one first.");
                }
            }
        }

        if (!ModelState.IsValid)
        {
            await BuildPanelsAsync(post, cancellationToken);
            return Page();
        }

        post ??= new Post
        {
            UserId = userId,
            OrganizationId = user.OrganizationId
        };

        if (post.Id == default)
        {
            post.Id = Guid.NewGuid();
        }

        post.Title = Title.Trim();
        post.MasterBody = MasterBody;
        post.UpdatedUtc = DateTimeOffset.UtcNow;

        foreach (var input in Targets)
        {
            var target = post.Targets.FirstOrDefault(t => t.Platform == input.Platform);
            if (target is null)
            {
                target = new PostTarget
                {
                    PostId = post.Id,
                    UserId = userId,
                    Platform = input.Platform
                };
                post.Targets.Add(target);
            }

            // Never rewrite something that already went out. Editing a published post edits the
            // record here, not the live post, and the publisher skips anything already done.
            if (target.Status == TargetStatus.Published)
            {
                continue;
            }

            target.Enabled = input.Enabled;
            target.Body = input.Body?.Trim() ?? string.Empty;
            target.ImageId = input.ImageId;
            target.ImageAltText = input.ImageAltText;
            target.Status = input.Enabled ? TargetStatus.Pending : TargetStatus.Skipped;
            target.Attempts = 0;
            target.NextAttemptUtc = null;
            target.ErrorMessage = null;
        }

        if (db.Entry(post).State == EntityState.Detached)
        {
            db.Posts.Add(post);
        }

        switch (action)
        {
            case SubmitAction.SaveDraft:
                post.Status = PostStatus.Draft;
                post.ScheduledUtc = ScheduledLocal is { } local
                    ? AppTimeZone.ToUtc(local, TimeZoneId)
                    : null;
                await db.SaveChangesAsync(cancellationToken);
                StatusMessage = "Saved as a draft.";
                return Redirect($"/app/posts/{post.Id}");

            case SubmitAction.Schedule:
                post.Status = PostStatus.Scheduled;
                post.ScheduledUtc = scheduledUtc;
                await db.SaveChangesAsync(cancellationToken);
                StatusMessage = $"Scheduled for {AppTimeZone.ToLocal(scheduledUtc!.Value, TimeZoneId):ddd d MMM, h:mm tt} in {TimeZoneId}.";
                return Redirect($"/app/posts/{post.Id}");

            default:
                post.ScheduledUtc = null;
                post.Status = PostStatus.Draft;
                await db.SaveChangesAsync(cancellationToken);

                // Same claim and publish path the scheduler uses, so there is one code path.
                if (await publisher.TryClaimAsync(post.Id, [PostStatus.Draft, PostStatus.Scheduled], cancellationToken))
                {
                    await publisher.PublishClaimedPostAsync(post.Id, cancellationToken);
                }
                else
                {
                    logger.LogWarning("Post {PostId} could not be claimed for an immediate publish.", post.Id);
                }

                StatusMessage = "Published. Per platform results are below.";
                return Redirect($"/app/posts/{post.Id}");
        }
    }

    /// <summary>
    /// Copies the master draft into every enabled platform body. When a body already has text,
    /// an inline confirm panel is swapped in first rather than a browser dialog.
    /// </summary>
    public async Task<IActionResult> OnPostCopyMasterAsync(bool confirmed, CancellationToken cancellationToken)
    {
        await LoadTimeZoneAsync(cancellationToken);

        var enabled = Targets.Where(t => t.Enabled).ToList();

        if (enabled.Count == 0)
        {
            ViewData["CopyMessage"] = "Nothing is enabled yet, so there is nowhere to copy to.";
            return this.PartialWithViewData("_CopyResult");
        }

        if (string.IsNullOrWhiteSpace(MasterBody))
        {
            ViewData["CopyMessage"] = "The master draft is empty.";
            return this.PartialWithViewData("_CopyResult");
        }

        var wouldOverwrite = enabled
            .Where(t => !string.IsNullOrWhiteSpace(t.Body) && t.Body!.Trim() != MasterBody.Trim())
            .Select(t => registry.Get(t.Platform).Capabilities.DisplayName)
            .ToList();

        if (wouldOverwrite.Count > 0 && !confirmed)
        {
            ViewData["Overwrites"] = wouldOverwrite;
            return this.PartialWithViewData("_CopyConfirm");
        }

        foreach (var target in enabled)
        {
            target.Body = MasterBody;
        }

        await BuildPanelsAsync(null, cancellationToken);
        ViewData["CopyMessage"] = $"Copied into {enabled.Count} platform{(enabled.Count == 1 ? "" : "s")}.";
        return this.PartialWithViewData("_CopyResult");
    }

    /// <summary>Empties the copy result area when the overwrite confirm is dismissed.</summary>
    public IActionResult OnGetClearCopy() => Content(string.Empty, "text/html");

    /// <summary>
    /// Uploads one image and swaps the slot back. Images are stored standalone, so this works
    /// on a post that has not been saved yet.
    /// </summary>
    public async Task<IActionResult> OnPostUploadImageAsync(
        [FromQuery] int index, IFormFile? file, CancellationToken cancellationToken)
    {
        await LoadTimeZoneAsync(cancellationToken);

        var userId = currentUser.RequireUserId();
        var user = await db.LoadWithOrganizationAsync(userId, cancellationToken)
                   ?? throw new InvalidOperationException("The signed in user is missing.");

        EnsureTargets();

        if (index < 0 || index >= Targets.Count)
        {
            return BadRequest();
        }

        if (file is null || file.Length == 0)
        {
            ViewData["SlotError"] = "Pick a file first.";
            return await SlotPartialAsync(index, cancellationToken);
        }

        await using var stream = file.OpenReadStream();
        var result = await images.UploadAsync(
            userId, user.OrganizationId, file.FileName, stream, file.Length, cancellationToken);

        if (!result.Success)
        {
            ViewData["SlotError"] = result.Error;
            return await SlotPartialAsync(index, cancellationToken);
        }

        Targets[index].ImageId = result.Image!.Id;
        return await SlotPartialAsync(index, cancellationToken);
    }

    public async Task<IActionResult> OnPostRemoveImageAsync([FromQuery] int index, CancellationToken cancellationToken)
    {
        await LoadTimeZoneAsync(cancellationToken);
        EnsureTargets();

        if (index < 0 || index >= Targets.Count)
        {
            return BadRequest();
        }

        Targets[index].ImageId = null;
        Targets[index].ImageAltText = null;
        return await SlotPartialAsync(index, cancellationToken);
    }

    private async Task<IActionResult> SlotPartialAsync(int index, CancellationToken cancellationToken)
    {
        await BuildPanelsAsync(null, cancellationToken);
        ViewData["SlotIndex"] = index;
        return this.PartialWithViewData("_ImageSlot");
    }

    private async Task LoadTimeZoneAsync(CancellationToken cancellationToken)
    {
        var user = await db.LoadWithOrganizationAsync(currentUser.RequireUserId(), cancellationToken);
        TimeZoneId = user?.TimeZoneId ?? AppTimeZone.Default;
    }

    /// <summary>
    /// Model binding stops at the first missing index, so a form that only posts some panels
    /// can arrive short. Fill the list back out in the canonical platform order.
    /// </summary>
    private void EnsureTargets()
    {
        var byPlatform = new Dictionary<SocialPlatform, TargetInput>();
        foreach (var target in Targets)
        {
            // Model binding produces a sparse list when a form posts only some of the indexes.
            if (target is not null && target.Platform != default)
            {
                byPlatform[target.Platform] = target;
            }
        }

        Targets = registry.All
            .Select(p => byPlatform.GetValueOrDefault(p.Platform) ?? new TargetInput { Platform = p.Platform })
            .ToList();
    }

    private async Task BuildPanelsAsync(Post? post, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();

        EnsureTargets();

        var imageIds = Targets.Where(t => t.ImageId is not null).Select(t => t.ImageId!.Value).ToList();
        var loaded = imageIds.Count == 0
            ? []
            : await db.StoredImages.Where(i => imageIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);

        var accounts = await db.SocialAccounts
            .Where(a => a.UserId == userId)
            .ToDictionaryAsync(a => a.Platform, cancellationToken);

        Panels = Targets.Select((input, index) =>
        {
            var capabilities = registry.Get(input.Platform).Capabilities;
            var account = accounts.GetValueOrDefault(input.Platform);

            return new Panel(
                index,
                capabilities,
                input,
                input.ImageId is { } imageId ? loaded.GetValueOrDefault(imageId) : null,
                account?.CharacterLimitOverride ?? capabilities.DefaultCharacterLimit,
                account?.Status == AccountStatus.Connected,
                post?.Targets.FirstOrDefault(t => t.Platform == input.Platform));
        }).ToList();
    }
}
