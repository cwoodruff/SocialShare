using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Core.Services;
using SocialShare.Tests.Support;

namespace SocialShare.Tests;

/// <summary>
/// Drives the worker a tick at a time rather than waiting on its timer, so the scheduling
/// behaviour is provable in milliseconds instead of minutes.
/// </summary>
public class SchedulerBackgroundServiceTests
{
    [Fact]
    public async Task A_post_scheduled_in_the_future_is_left_alone()
    {
        using var harness = new SchedulerHarness();
        await harness.SeedScheduledPostAsync(DateTimeOffset.UtcNow.AddMinutes(2));

        await harness.Scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PostStatus.Scheduled, await harness.StatusAsync());
        Assert.Empty(harness.Platform.Published);
    }

    [Fact]
    public async Task A_post_whose_time_has_come_publishes_without_anybody_touching_it()
    {
        using var harness = new SchedulerHarness();
        await harness.SeedScheduledPostAsync(DateTimeOffset.UtcNow.AddMinutes(2));

        // Wind the clock forward by moving the post rather than waiting two real minutes.
        await harness.MoveScheduleAsync(DateTimeOffset.UtcNow.AddSeconds(-1));
        await harness.Scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PostStatus.Published, await harness.StatusAsync());
        Assert.Single(harness.Platform.Published);
    }

    [Fact]
    public async Task Two_ticks_over_the_same_post_publish_it_once()
    {
        using var harness = new SchedulerHarness();
        await harness.SeedScheduledPostAsync(DateTimeOffset.UtcNow.AddSeconds(-1));

        await harness.Scheduler.RunOnceAsync(CancellationToken.None);
        await harness.Scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Single(harness.Platform.Published);
    }

    [Fact]
    public async Task A_post_left_mid_publish_by_a_restart_is_released_and_retried()
    {
        using var harness = new SchedulerHarness();
        var postId = await harness.SeedScheduledPostAsync(DateTimeOffset.UtcNow.AddSeconds(-1));

        await using (var db = harness.NewContext())
        {
            await db.Posts.IgnoreQueryFilters().Where(p => p.Id == postId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, PostStatus.Publishing)
                    .SetProperty(p => p.ClaimedUtc, DateTimeOffset.UtcNow.AddHours(-1)));
        }

        await harness.Scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PostStatus.Published, await harness.StatusAsync());
        Assert.Single(harness.Platform.Published);
    }

    [Fact]
    public async Task A_failed_platform_is_retried_once_its_backoff_has_passed()
    {
        using var harness = new SchedulerHarness();
        await harness.SeedScheduledPostAsync(DateTimeOffset.UtcNow.AddSeconds(-1));

        harness.Platform.OnPublish = _ => new PlatformPublishResult(false, Error: "Temporary.");
        await harness.Scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PostStatus.Failed, await harness.StatusAsync());

        // Nothing is due yet, so a tick right now changes nothing.
        await harness.Scheduler.RunOnceAsync(CancellationToken.None);
        Assert.Single(harness.Platform.Published);

        await harness.MakeRetryDueAsync();
        harness.Platform.OnPublish = _ => new PlatformPublishResult(true, "r1", "https://example.test/p/1");
        await harness.Scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PostStatus.Published, await harness.StatusAsync());
        Assert.Equal(2, harness.Platform.Published.Count);
    }

    private sealed class SchedulerHarness : IDisposable
    {
        private readonly TestDatabase _database = new();
        private readonly ServiceProvider _services;
        private Guid _postId;

        public SchedulerHarness()
        {
            Platform = new FakePlatform(SocialPlatform.Bluesky);

            var services = new ServiceCollection();
            services.AddSingleton(_database.Options);
            services.AddScoped<TenantContext>();
            services.AddScoped(provider => new SocialShareDbContext(
                _database.Options, provider.GetRequiredService<TenantContext>()));
            services.AddScoped<ISocialPlatform>(_ => Platform);
            services.AddScoped<ISocialPlatformRegistry, SocialPlatformRegistry>();
            services.AddScoped<ISecretProtector, PassThroughProtector>();
            services.AddScoped<SecretVault>();
            services.AddSingleton<IImageStore, InMemoryImageStore>();
            services.AddScoped<PublishingService>();
            services.AddSingleton(new OptionsWrapper<SchedulerOptions>(SchedulerOptions) as IOptions<SchedulerOptions>);
            services.AddSingleton(new OptionsWrapper<RetentionOptions>(new RetentionOptions()) as IOptions<RetentionOptions>);
            services.AddSingleton(new OptionsWrapper<AppOptions>(
                new AppOptions { PublicBaseUrl = "https://app.test" }) as IOptions<AppOptions>);
            services.AddLogging();

            _services = services.BuildServiceProvider();

            Scheduler = new SchedulerBackgroundService(
                _services.GetRequiredService<IServiceScopeFactory>(),
                _services.GetRequiredService<IOptions<SchedulerOptions>>(),
                _services.GetRequiredService<IOptions<RetentionOptions>>(),
                NullLogger<SchedulerBackgroundService>.Instance);
        }

        private static SchedulerOptions SchedulerOptions => new()
        {
            PollSeconds = 1,
            MaxAttempts = 3,
            RetryBackoffSeconds = [60],
            PublishTimeoutSeconds = 5,
            StuckClaimMinutes = 15
        };

        public FakePlatform Platform { get; }

        public SchedulerBackgroundService Scheduler { get; }

        public SocialShareDbContext NewContext() => _database.NewContext();

        public async Task<Guid> SeedScheduledPostAsync(DateTimeOffset scheduledUtc)
        {
            await using var db = _database.NewContext();

            var organization = new Organization { Name = "Test" };
            db.Organizations.Add(organization);

            var userId = Guid.NewGuid();

            var post = new Post
            {
                UserId = userId,
                OrganizationId = organization.Id,
                Title = "Scheduled",
                Status = PostStatus.Scheduled,
                ScheduledUtc = scheduledUtc
            };

            post.Targets.Add(new PostTarget
            {
                PostId = post.Id,
                UserId = userId,
                Platform = SocialPlatform.Bluesky,
                Enabled = true,
                Body = "Going out on its own"
            });

            db.SocialAccounts.Add(new SocialAccount
            {
                UserId = userId,
                OrganizationId = organization.Id,
                Platform = SocialPlatform.Bluesky,
                Status = AccountStatus.Connected,
                TokensCipher = "plain:{\"accessToken\":\"token\"}",
                TokenExpiresUtc = DateTimeOffset.UtcNow.AddHours(5)
            });

            db.Posts.Add(post);
            await db.SaveChangesAsync();

            _postId = post.Id;
            return post.Id;
        }

        public async Task MoveScheduleAsync(DateTimeOffset scheduledUtc)
        {
            await using var db = _database.NewContext();
            await db.Posts.IgnoreQueryFilters().Where(p => p.Id == _postId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ScheduledUtc, scheduledUtc));
        }

        public async Task MakeRetryDueAsync()
        {
            await using var db = _database.NewContext();
            await db.PostTargets.IgnoreQueryFilters().Where(t => t.PostId == _postId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.NextAttemptUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        public async Task<PostStatus> StatusAsync()
        {
            await using var db = _database.NewContext();
            return await db.Posts.IgnoreQueryFilters().Where(p => p.Id == _postId).Select(p => p.Status).SingleAsync();
        }

        public void Dispose()
        {
            _services.Dispose();
            _database.Dispose();
        }
    }
}
