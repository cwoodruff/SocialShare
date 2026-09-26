using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Domain;
using SocialShare.Tests.Support;

namespace SocialShare.Tests;

/// <summary>
/// The claim is what stops a restart from double posting, so it is worth proving that the
/// atomic update really is atomic and really does translate on SQLite.
/// </summary>
public class SchedulerClaimTests
{
    [Fact]
    public async Task Claiming_a_scheduled_post_updates_exactly_one_row()
    {
        using var database = new TestDatabase();
        var postId = await SeedScheduledPostAsync(database);

        await using var db = database.NewContext();
        var now = DateTimeOffset.UtcNow;

        var claimed = await db.Posts
            .IgnoreQueryFilters()
            .Where(p => p.Id == postId && p.Status == PostStatus.Scheduled)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, PostStatus.Publishing)
                      .SetProperty(p => p.ClaimedUtc, now));

        Assert.Equal(1, claimed);
    }

    [Fact]
    public async Task Releasing_a_stuck_claim_can_null_a_timestamp()
    {
        using var database = new TestDatabase();
        var postId = await SeedScheduledPostAsync(database);

        await using var db = database.NewContext();
        await db.Posts.IgnoreQueryFilters()
            .Where(p => p.Id == postId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, PostStatus.Publishing)
                      .SetProperty(p => p.ClaimedUtc, DateTimeOffset.UtcNow.AddHours(-1)));

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);

        var released = await db.Posts
            .IgnoreQueryFilters()
            .Where(p => p.Status == PostStatus.Publishing && p.ClaimedUtc != null && p.ClaimedUtc < cutoff)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, PostStatus.Scheduled)
                      .SetProperty(p => p.ClaimedUtc, (DateTimeOffset?)null));

        Assert.Equal(1, released);
    }

    private static async Task<Guid> SeedScheduledPostAsync(TestDatabase database)
    {
        await using var db = database.NewContext();

        var organization = new Organization { Name = "Test" };
        db.Organizations.Add(organization);

        var post = new Post
        {
            UserId = Guid.NewGuid(),
            OrganizationId = organization.Id,
            Title = "Scheduled",
            Status = PostStatus.Scheduled,
            ScheduledUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        db.Posts.Add(post);
        await db.SaveChangesAsync();
        return post.Id;
    }
}
