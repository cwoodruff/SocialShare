using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;

namespace SocialShare.Core.Services;

/// <summary>
/// The scheduler. It polls SQLite on a short interval, claims anything due, publishes it, and
/// retries individual platforms that failed. On Azure App Service this only runs reliably with
/// Always On enabled, which needs a B1 plan or better.
/// </summary>
public sealed class SchedulerBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<SchedulerOptions> schedulerOptions,
    IOptions<RetentionOptions> retentionOptions,
    ILogger<SchedulerBackgroundService> logger) : BackgroundService
{
    private readonly SchedulerOptions _options = schedulerOptions.Value;
    private readonly RetentionOptions _retention = retentionOptions.Value;
    private DateTimeOffset _lastMaintenanceUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogWarning("The scheduler is disabled by configuration. Scheduled posts will not publish.");
            return;
        }

        logger.LogInformation("Scheduler started, polling every {Seconds} seconds.", _options.PollSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A bad tick must never kill the loop, or nothing publishes until the next restart.
                logger.LogError(ex, "Scheduler tick failed.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Scheduler stopped.");
    }

    /// <summary>One pass. Public so a test can drive it without waiting on the timer.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await ReleaseStuckClaimsAsync(cancellationToken);
        await PublishDuePostsAsync(cancellationToken);
        await RetryFailedTargetsAsync(cancellationToken);
        await RunMaintenanceAsync(cancellationToken);
    }

    private async Task PublishDuePostsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        List<Guid> due;
        using (var scope = CreateSystemScope(out var db))
        {
            due = await db.Posts
                .Where(p => p.Status == PostStatus.Scheduled && p.ScheduledUtc != null && p.ScheduledUtc <= now)
                .OrderBy(p => p.ScheduledUtc)
                .Select(p => p.Id)
                .Take(25)
                .ToListAsync(cancellationToken);
        }

        foreach (var postId in due)
        {
            using var scope = CreateSystemScope(out var db);
            var publisher = scope.ServiceProvider.GetRequiredService<PublishingService>();

            if (!await publisher.TryClaimAsync(postId, [PostStatus.Scheduled], cancellationToken))
            {
                continue;
            }

            logger.LogInformation("Claimed scheduled post {PostId}.", postId);
            await publisher.PublishClaimedPostAsync(postId, cancellationToken);
        }
    }

    private async Task RetryFailedTargetsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        List<Guid> targetIds;
        using (var scope = CreateSystemScope(out var db))
        {
            targetIds = await db.PostTargets
                .Where(t => t.Enabled
                    && t.Status == TargetStatus.Failed
                    && t.Attempts < _options.MaxAttempts
                    && t.NextAttemptUtc != null
                    && t.NextAttemptUtc <= now)
                .OrderBy(t => t.NextAttemptUtc)
                .Select(t => t.Id)
                .Take(25)
                .ToListAsync(cancellationToken);
        }

        foreach (var targetId in targetIds)
        {
            using var scope = CreateSystemScope(out _);
            var publisher = scope.ServiceProvider.GetRequiredService<PublishingService>();
            logger.LogInformation("Retrying platform target {TargetId}.", targetId);
            await publisher.RetryTargetAsync(targetId, cancellationToken);
        }
    }

    /// <summary>
    /// If the process died mid publish, a post is left sitting in Publishing forever. Anything
    /// claimed longer ago than the timeout goes back so it gets another chance.
    /// </summary>
    private async Task ReleaseStuckClaimsAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(_options.StuckClaimMinutes);
        using var scope = CreateSystemScope(out var db);

        var released = await db.Posts
            .Where(p => p.Status == PostStatus.Publishing && p.ClaimedUtc != null && p.ClaimedUtc < cutoff)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, PostStatus.Scheduled).SetProperty(p => p.ClaimedUtc, (DateTimeOffset?)null),
                cancellationToken);

        if (released > 0)
        {
            logger.LogWarning("Released {Count} post(s) that were stuck in Publishing.", released);
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        if (!_retention.PurgeEnabled || DateTimeOffset.UtcNow - _lastMaintenanceUtc < TimeSpan.FromHours(6))
        {
            return;
        }

        _lastMaintenanceUtc = DateTimeOffset.UtcNow;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retention.PurgePublishedAfterDays);

        using var scope = CreateSystemScope(out var db);
        var deleted = await db.Posts
            .Where(p => p.Status == PostStatus.Published && p.PublishedUtc != null && p.PublishedUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            logger.LogInformation("Purged {Count} published post(s) older than {Days} days.",
                deleted, _retention.PurgePublishedAfterDays);
        }
    }

    private IServiceScope CreateSystemScope(out SocialShareDbContext db)
    {
        var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystem();
        db = scope.ServiceProvider.GetRequiredService<SocialShareDbContext>();
        return scope;
    }
}
