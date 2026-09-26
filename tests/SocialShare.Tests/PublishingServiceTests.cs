using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Core.Services;
using SocialShare.Tests.Support;

namespace SocialShare.Tests;

public class PublishingServiceTests
{
    private static readonly SchedulerOptions Options = new()
    {
        MaxAttempts = 3,
        RetryBackoffSeconds = [60, 300, 900],
        PublishTimeoutSeconds = 5
    };

    private static PublishingService NewService(
        SocialShareDbContext db, params FakePlatform[] platforms) =>
        new(db,
            new SocialPlatformRegistry(platforms),
            new SecretVault(new PassThroughProtector()),
            new InMemoryImageStore(),
            new OptionsWrapper<SchedulerOptions>(Options),
            new OptionsWrapper<AppOptions>(new AppOptions { PublicBaseUrl = "https://app.test" }),
            NullLogger<PublishingService>.Instance);

    [Fact]
    public async Task A_post_can_only_be_claimed_once()
    {
        using var database = new TestDatabase();
        var (postId, _) = await SeedAsync(database, SocialPlatform.Bluesky);

        await using var db = database.NewContext();
        var service = NewService(db, new FakePlatform(SocialPlatform.Bluesky));

        Assert.True(await service.TryClaimAsync(postId, [PostStatus.Scheduled], CancellationToken.None));

        // This is the guard that stops a restart mid publish from posting the same thing twice.
        Assert.False(await service.TryClaimAsync(postId, [PostStatus.Scheduled], CancellationToken.None));
    }

    [Fact]
    public async Task Publishing_records_the_remote_url_and_writes_a_log_row()
    {
        using var database = new TestDatabase();
        var (postId, _) = await SeedAsync(database, SocialPlatform.Bluesky);

        await using var db = database.NewContext();
        var platform = new FakePlatform(SocialPlatform.Bluesky);
        var service = NewService(db, platform);

        await service.TryClaimAsync(postId, [PostStatus.Scheduled], CancellationToken.None);
        await service.PublishClaimedPostAsync(postId, CancellationToken.None);

        await using var check = database.NewContext();
        var post = await check.Posts.Include(p => p.Targets).SingleAsync(p => p.Id == postId);
        var target = post.Targets.Single(t => t.Platform == SocialPlatform.Bluesky);

        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal(TargetStatus.Published, target.Status);
        Assert.Equal("https://example.test/p/1", target.RemoteUrl);
        Assert.NotNull(post.PublishedUtc);

        var log = await check.PublishLogs.SingleAsync();
        Assert.True(log.Success);
        Assert.Equal(1, log.AttemptNumber);
    }

    [Fact]
    public async Task A_failure_is_captured_verbatim_and_a_retry_is_queued_with_backoff()
    {
        using var database = new TestDatabase();
        var (postId, _) = await SeedAsync(database, SocialPlatform.Bluesky);

        await using var db = database.NewContext();
        var platform = new FakePlatform(SocialPlatform.Bluesky)
        {
            OnPublish = _ => new PlatformPublishResult(false, Error: "HTTP 429 Too Many Requests. Slow down.", HttpStatusCode: 429)
        };

        var service = NewService(db, platform);
        await service.TryClaimAsync(postId, [PostStatus.Scheduled], CancellationToken.None);
        await service.PublishClaimedPostAsync(postId, CancellationToken.None);

        await using var check = database.NewContext();
        var target = await check.PostTargets.SingleAsync(t => t.Platform == SocialPlatform.Bluesky);

        Assert.Equal(TargetStatus.Failed, target.Status);
        Assert.Equal("HTTP 429 Too Many Requests. Slow down.", target.ErrorMessage);
        Assert.Equal(1, target.Attempts);
        Assert.NotNull(target.NextAttemptUtc);
        Assert.InRange(
            target.NextAttemptUtc.Value,
            DateTimeOffset.UtcNow.AddSeconds(50),
            DateTimeOffset.UtcNow.AddSeconds(70));

        Assert.Equal(429, (await check.PublishLogs.SingleAsync()).HttpStatusCode);
    }

    [Fact]
    public async Task Automatic_retries_stop_after_the_configured_attempts()
    {
        using var database = new TestDatabase();
        var (postId, _) = await SeedAsync(database, SocialPlatform.Bluesky);

        var platform = new FakePlatform(SocialPlatform.Bluesky)
        {
            OnPublish = _ => new PlatformPublishResult(false, Error: "Still broken.")
        };

        Guid targetId;
        await using (var db = database.NewContext())
        {
            var service = NewService(db, platform);
            await service.TryClaimAsync(postId, [PostStatus.Scheduled], CancellationToken.None);
            await service.PublishClaimedPostAsync(postId, CancellationToken.None);
            targetId = await db.PostTargets.Where(t => t.PostId == postId).Select(t => t.Id).SingleAsync();
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = database.NewContext();
            await NewService(db, platform).RetryTargetAsync(targetId, CancellationToken.None);
        }

        await using var check = database.NewContext();
        var target = await check.PostTargets.SingleAsync();

        Assert.Equal(3, target.Attempts);

        // Three strikes and it waits for a human rather than hammering the platform.
        Assert.Null(target.NextAttemptUtc);
        Assert.Equal(PostStatus.Failed, (await check.Posts.SingleAsync()).Status);
    }

    [Fact]
    public async Task One_platform_failing_leaves_the_post_partly_published()
    {
        using var database = new TestDatabase();
        var (postId, _) = await SeedAsync(database, SocialPlatform.Bluesky, SocialPlatform.Mastodon);

        await using var db = database.NewContext();
        var good = new FakePlatform(SocialPlatform.Bluesky);
        var bad = new FakePlatform(SocialPlatform.Mastodon)
        {
            OnPublish = _ => new PlatformPublishResult(false, Error: "The instance is down.")
        };

        var service = NewService(db, good, bad);
        await service.TryClaimAsync(postId, [PostStatus.Scheduled], CancellationToken.None);
        await service.PublishClaimedPostAsync(postId, CancellationToken.None);

        await using var check = database.NewContext();
        var post = await check.Posts.Include(p => p.Targets).SingleAsync();

        Assert.Equal(PostStatus.PartiallyPublished, post.Status);
        Assert.Equal(TargetStatus.Published, post.Targets.Single(t => t.Platform == SocialPlatform.Bluesky).Status);
        Assert.Equal(TargetStatus.Failed, post.Targets.Single(t => t.Platform == SocialPlatform.Mastodon).Status);
    }

    [Fact]
    public async Task A_platform_that_demands_an_image_is_refused_before_the_call_is_made()
    {
        using var database = new TestDatabase();
        var (postId, _) = await SeedAsync(database, SocialPlatform.Instagram);

        await using var db = database.NewContext();
        var platform = new FakePlatform(SocialPlatform.Instagram);
        platform.Capabilities = platform.Capabilities with { ImageRequired = true };

        var service = NewService(db, platform);
        await service.TryClaimAsync(postId, [PostStatus.Scheduled], CancellationToken.None);
        await service.PublishClaimedPostAsync(postId, CancellationToken.None);

        await using var check = database.NewContext();
        var target = await check.PostTargets.SingleAsync();

        Assert.Equal(TargetStatus.Failed, target.Status);
        Assert.Contains("without an image", target.ErrorMessage);
        Assert.Empty(platform.Published);
    }

    [Fact]
    public async Task A_body_over_the_limit_never_reaches_the_platform()
    {
        using var database = new TestDatabase();
        var (postId, _) = await SeedAsync(database, SocialPlatform.Bluesky, body: new string('x', 400));

        await using var db = database.NewContext();
        var platform = new FakePlatform(SocialPlatform.Bluesky);

        var service = NewService(db, platform);
        await service.TryClaimAsync(postId, [PostStatus.Scheduled], CancellationToken.None);
        await service.PublishClaimedPostAsync(postId, CancellationToken.None);

        await using var check = database.NewContext();
        Assert.Contains("400 characters", (await check.PostTargets.SingleAsync()).ErrorMessage);
        Assert.Empty(platform.Published);
    }

    [Fact]
    public async Task An_exception_from_a_platform_is_reported_rather_than_swallowed()
    {
        using var database = new TestDatabase();
        var (postId, _) = await SeedAsync(database, SocialPlatform.Bluesky);

        await using var db = database.NewContext();
        var platform = new FakePlatform(SocialPlatform.Bluesky)
        {
            OnPublish = _ => throw new HttpRequestException("Connection reset by peer")
        };

        var service = NewService(db, platform);
        await service.TryClaimAsync(postId, [PostStatus.Scheduled], CancellationToken.None);
        await service.PublishClaimedPostAsync(postId, CancellationToken.None);

        await using var check = database.NewContext();
        var target = await check.PostTargets.SingleAsync();

        Assert.Equal(TargetStatus.Failed, target.Status);
        Assert.Contains("HttpRequestException", target.ErrorMessage);
        Assert.Contains("Connection reset by peer", target.ErrorMessage);
    }

    [Fact]
    public async Task An_already_published_platform_is_not_published_again_on_retry()
    {
        using var database = new TestDatabase();
        var (postId, _) = await SeedAsync(database, SocialPlatform.Bluesky, SocialPlatform.Mastodon);

        var good = new FakePlatform(SocialPlatform.Bluesky);
        var bad = new FakePlatform(SocialPlatform.Mastodon)
        {
            OnPublish = _ => new PlatformPublishResult(false, Error: "Down.")
        };

        await using (var db = database.NewContext())
        {
            var service = NewService(db, good, bad);
            await service.TryClaimAsync(postId, [PostStatus.Scheduled], CancellationToken.None);
            await service.PublishClaimedPostAsync(postId, CancellationToken.None);
        }

        await using (var db = database.NewContext())
        {
            await NewService(db, good, bad).PublishClaimedPostAsync(postId, CancellationToken.None);
        }

        Assert.Single(good.Published);
        Assert.Equal(2, bad.Published.Count);
    }

    [Fact]
    public async Task An_image_is_handed_over_with_a_public_url_for_the_platforms_that_need_one()
    {
        using var database = new TestDatabase();
        var (postId, userId) = await SeedAsync(database, SocialPlatform.Threads);

        await using (var seed = database.NewContext())
        {
            var image = new StoredImage
            {
                UserId = userId,
                OrganizationId = (await seed.Organizations.FirstAsync()).Id,
                OriginalFileName = "photo.png",
                StorageKey = "ab/cd/deadbeef.png",
                ContentType = "image/png",
                ByteSize = 100,
                Width = 1080,
                Height = 1080
            };

            seed.StoredImages.Add(image);
            var target = await seed.PostTargets.SingleAsync();
            target.ImageId = image.Id;
            target.ImageAltText = "A square";
            await seed.SaveChangesAsync();
        }

        await using var db = database.NewContext();
        var platform = new FakePlatform(SocialPlatform.Threads);
        platform.Capabilities = platform.Capabilities with { RequiresPublicImageUrl = true };

        var service = NewService(db, platform);
        await service.TryClaimAsync(postId, [PostStatus.Scheduled], CancellationToken.None);
        await service.PublishClaimedPostAsync(postId, CancellationToken.None);

        var sent = Assert.Single(platform.Published);
        Assert.NotNull(sent.Image);
        Assert.Equal("https://app.test/i/ab/cd/deadbeef.png", sent.Image.PublicUrl);
        Assert.Equal("A square", sent.Image.AltText);
    }

    private static async Task<(Guid PostId, Guid UserId)> SeedAsync(
        TestDatabase database, params SocialPlatform[] platforms) =>
        await SeedAsync(database, "A body", platforms);

    private static Task<(Guid PostId, Guid UserId)> SeedAsync(
        TestDatabase database, SocialPlatform platform, string body) =>
        SeedAsync(database, body, [platform]);

    private static async Task<(Guid PostId, Guid UserId)> SeedAsync(
        TestDatabase database, string body, SocialPlatform[] platforms)
    {
        await using var db = database.NewContext();

        var organization = new Organization { Name = "Test" };
        db.Organizations.Add(organization);

        var userId = Guid.NewGuid();

        var post = new Post
        {
            UserId = userId,
            OrganizationId = organization.Id,
            Title = "Test post",
            Status = PostStatus.Scheduled,
            ScheduledUtc = DateTimeOffset.UtcNow.AddSeconds(-5)
        };

        foreach (var platform in platforms)
        {
            post.Targets.Add(new PostTarget
            {
                PostId = post.Id,
                UserId = userId,
                Platform = platform,
                Enabled = true,
                Body = body
            });

            db.SocialAccounts.Add(new SocialAccount
            {
                UserId = userId,
                OrganizationId = organization.Id,
                Platform = platform,
                Status = AccountStatus.Connected,
                DisplayName = "@fake",
                RemoteAccountId = "remote-account",
                TokensCipher = "plain:{\"accessToken\":\"token\"}",
                TokenExpiresUtc = DateTimeOffset.UtcNow.AddHours(5)
            });
        }

        db.Posts.Add(post);
        await db.SaveChangesAsync();

        return (post.Id, userId);
    }
}
