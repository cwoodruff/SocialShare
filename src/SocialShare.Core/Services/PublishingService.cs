using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;

namespace SocialShare.Core.Services;

/// <summary>
/// Publishes a post to every platform it is enabled for. Used by both the Publish now button and
/// the background scheduler, so there is exactly one code path that talks to the platforms.
/// </summary>
public sealed class PublishingService(
    SocialShareDbContext db,
    ISocialPlatformRegistry registry,
    SecretVault vault,
    IImageStore imageStore,
    IOptions<SchedulerOptions> schedulerOptions,
    IOptions<AppOptions> appOptions,
    ILogger<PublishingService> logger)
{
    private readonly SchedulerOptions _scheduler = schedulerOptions.Value;
    private readonly AppOptions _app = appOptions.Value;

    /// <summary>
    /// Moves a post to Publishing if and only if it is currently in one of the given states.
    /// This is the guard that stops a restart, a second worker or an impatient click from
    /// publishing the same post twice.
    /// </summary>
    public async Task<bool> TryClaimAsync(Guid postId, PostStatus[] allowedFrom, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // The filters are bypassed here because ExecuteUpdate cannot translate them. The post id
        // has already been resolved through a filtered query by every caller, and the status
        // predicate is what makes the claim safe.
        var claimed = await db.Posts
            .IgnoreQueryFilters()
            .Where(p => p.Id == postId && allowedFrom.Contains(p.Status))
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, PostStatus.Publishing)
                      .SetProperty(p => p.ClaimedUtc, now)
                      .SetProperty(p => p.UpdatedUtc, now),
                cancellationToken);

        return claimed == 1;
    }

    /// <summary>
    /// Publishes every enabled target of an already claimed post, then recomputes the post status.
    /// Each platform is independent: one failure never stops the others.
    /// </summary>
    public async Task PublishClaimedPostAsync(Guid postId, CancellationToken cancellationToken)
    {
        var post = await db.Posts
            .Include(p => p.Targets)
            .ThenInclude(t => t.Image)
            .FirstOrDefaultAsync(p => p.Id == postId, cancellationToken);

        if (post is null)
        {
            logger.LogWarning("Post {PostId} vanished before publishing.", postId);
            return;
        }

        foreach (var target in post.Targets.Where(t => t.Enabled).OrderBy(t => (int)t.Platform))
        {
            if (target.Status == TargetStatus.Published)
            {
                continue;
            }

            await PublishTargetAsync(post, target, cancellationToken);
        }

        foreach (var target in post.Targets.Where(t => !t.Enabled))
        {
            target.Status = TargetStatus.Skipped;
        }

        ApplyPostStatus(post);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Publishes a single platform again, used by the retry button and the retry sweep.</summary>
    public async Task RetryTargetAsync(Guid targetId, CancellationToken cancellationToken)
    {
        var target = await db.PostTargets
            .Include(t => t.Image)
            .Include(t => t.Post)
            .ThenInclude(p => p!.Targets)
            .FirstOrDefaultAsync(t => t.Id == targetId, cancellationToken);

        if (target?.Post is null || target.Status == TargetStatus.Published)
        {
            return;
        }

        await PublishTargetAsync(target.Post, target, cancellationToken);
        ApplyPostStatus(target.Post);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task PublishTargetAsync(Post post, PostTarget target, CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        target.Status = TargetStatus.Publishing;
        target.Attempts++;
        target.ErrorMessage = null;

        var platform = registry.Get(target.Platform);
        PlatformPublishResult result;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_scheduler.PublishTimeoutSeconds));

        try
        {
            result = await PublishOnceAsync(platform, post, target, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new PlatformPublishResult(false,
                Error: $"{platform.Capabilities.DisplayName} did not answer within {_scheduler.PublishTimeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            // Never swallow a platform failure. It is logged and it is shown in the UI.
            logger.LogError(ex, "Publishing post {PostId} to {Platform} threw.", post.Id, target.Platform);
            result = new PlatformPublishResult(false, Error: $"{ex.GetType().Name}: {ex.Message}");
        }

        var completedUtc = DateTimeOffset.UtcNow;

        if (result.Success)
        {
            target.Status = TargetStatus.Published;
            target.RemotePostId = result.RemotePostId;
            target.RemoteUrl = result.RemoteUrl;
            target.PublishedUtc = completedUtc;
            target.NextAttemptUtc = null;
            logger.LogInformation(
                "Published post {PostId} to {Platform} as {RemotePostId}.",
                post.Id, target.Platform, result.RemotePostId);
        }
        else
        {
            target.Status = TargetStatus.Failed;
            target.ErrorMessage = Truncate(result.Error ?? "The platform rejected the post without saying why.", 4000);
            target.NextAttemptUtc = target.Attempts < _scheduler.MaxAttempts
                ? completedUtc + BackoffFor(target.Attempts)
                : null;
            logger.LogWarning(
                "Publishing post {PostId} to {Platform} failed on attempt {Attempt}: {Error}",
                post.Id, target.Platform, target.Attempts, target.ErrorMessage);
        }

        db.PublishLogs.Add(new PublishLog
        {
            UserId = post.UserId,
            PostId = post.Id,
            PostTargetId = target.Id,
            Platform = target.Platform,
            AttemptNumber = target.Attempts,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            Success = result.Success,
            HttpStatusCode = result.HttpStatusCode,
            Message = Truncate(result.Success ? result.RemoteUrl ?? "Published." : target.ErrorMessage!, 4000)
        });
    }

    private async Task<PlatformPublishResult> PublishOnceAsync(
        ISocialPlatform platform,
        Post post,
        PostTarget target,
        CancellationToken cancellationToken)
    {
        var account = await db.SocialAccounts
            .FirstOrDefaultAsync(a => a.UserId == post.UserId && a.Platform == target.Platform, cancellationToken);

        if (account is null || account.Status != AccountStatus.Connected)
        {
            return new PlatformPublishResult(false,
                Error: $"No connected {platform.Capabilities.DisplayName} account. Connect it on the accounts page and retry.");
        }

        var credentials = vault.ReadCredentials(account);
        var tokens = vault.ReadTokens(account);

        var refreshed = await platform.RefreshAsync(account, credentials, tokens, cancellationToken);
        if (refreshed is not null)
        {
            if (!refreshed.Success)
            {
                account.Status = AccountStatus.Error;
                account.LastError = refreshed.Error;
                account.UpdatedUtc = DateTimeOffset.UtcNow;
                return new PlatformPublishResult(false, Error: refreshed.Error ?? "Refreshing the stored token failed.");
            }

            if (refreshed.Tokens is not null)
            {
                tokens = refreshed.Tokens;
                vault.WriteTokens(account, refreshed.Tokens);
                account.TokenExpiresUtc = refreshed.TokenExpiresUtc;
                account.UpdatedUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        var capabilities = platform.Capabilities;

        if (capabilities.ImageRequired && target.Image is null)
        {
            return new PlatformPublishResult(false,
                Error: $"{capabilities.DisplayName} will not accept a post without an image.");
        }

        var limit = account.CharacterLimitOverride ?? capabilities.DefaultCharacterLimit;
        var length = Text.RichText.CountCharacters(target.Body);
        if (length > limit)
        {
            return new PlatformPublishResult(false,
                Error: $"That body is {length} characters and {capabilities.DisplayName} allows {limit}.");
        }

        PublishImage? image = null;
        if (target.Image is not null && capabilities.ImageSupported)
        {
            var stored = target.Image;
            image = new PublishImage(
                ct => imageStore.OpenReadAsync(stored.StorageKey, ct),
                stored.ContentType,
                stored.ByteSize,
                stored.Width,
                stored.Height,
                PublicImageUrl(stored.StorageKey),
                target.ImageAltText);
        }

        return await platform.PublishAsync(
            new PlatformPublishRequest(account, target.Body, image),
            credentials,
            tokens,
            cancellationToken);
    }

    public string PublicImageUrl(string storageKey)
    {
        var baseUrl = (_app.PublicBaseUrl ?? string.Empty).TrimEnd('/');
        return $"{baseUrl}/i/{storageKey}";
    }

    private TimeSpan BackoffFor(int attemptNumber)
    {
        var table = _scheduler.RetryBackoffSeconds;
        if (table.Length == 0)
        {
            return TimeSpan.FromMinutes(5);
        }

        var index = Math.Clamp(attemptNumber - 1, 0, table.Length - 1);
        return TimeSpan.FromSeconds(table[index]);
    }

    /// <summary>Rolls the per platform results up into the one status shown on the dashboard.</summary>
    public static void ApplyPostStatus(Post post)
    {
        var active = post.Targets.Where(t => t.Enabled).ToList();
        var now = DateTimeOffset.UtcNow;

        if (active.Count == 0)
        {
            post.Status = PostStatus.Draft;
            post.UpdatedUtc = now;
            return;
        }

        var published = active.Count(t => t.Status == TargetStatus.Published);
        var pending = active.Count(t => t.Status is TargetStatus.Pending or TargetStatus.Publishing);

        post.Status = pending > 0
            ? PostStatus.Publishing
            : published == active.Count
                ? PostStatus.Published
                : published > 0
                    ? PostStatus.PartiallyPublished
                    : PostStatus.Failed;

        if (published > 0 && post.PublishedUtc is null)
        {
            post.PublishedUtc = now;
        }

        if (post.Status != PostStatus.Publishing)
        {
            post.ClaimedUtc = null;
        }

        post.UpdatedUtc = now;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
