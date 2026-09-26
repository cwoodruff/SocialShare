using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Domain;

namespace SocialShare.Core.Data;

/// <summary>
/// The one database. Identity tables, tenants, accounts, posts, image metadata and publish logs
/// all live in the same SQLite file.
/// </summary>
public class SocialShareDbContext(DbContextOptions<SocialShareDbContext> options, TenantContext tenant)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    private readonly TenantContext _tenant = tenant;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<SocialAccount> SocialAccounts => Set<SocialAccount>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<PostTarget> PostTargets => Set<PostTarget>();
    public DbSet<StoredImage> StoredImages => Set<StoredImage>();
    public DbSet<PublishLog> PublishLogs => Set<PublishLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Organization>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Plan).HasConversion<string>().HasMaxLength(20);
        });

        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.Organization)
                .WithMany(o => o.Users)
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SocialAccount>(e =>
        {
            e.Property(x => x.Platform).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.RemoteAccountId).HasMaxLength(400);
            e.Property(x => x.LastError).HasMaxLength(4000);
            e.HasIndex(x => new { x.UserId, x.Platform }).IsUnique();
            e.HasQueryFilter(x => _tenant.RunAsSystem || x.UserId == _tenant.UserId);
        });

        builder.Entity<Post>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(25);
            e.HasIndex(x => new { x.UserId, x.Status });
            e.HasIndex(x => new { x.Status, x.ScheduledUtc });
            e.HasMany(x => x.Targets)
                .WithOne(t => t.Post!)
                .HasForeignKey(t => t.PostId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => _tenant.RunAsSystem || x.UserId == _tenant.UserId);
        });

        builder.Entity<PostTarget>(e =>
        {
            e.Property(x => x.Platform).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RemotePostId).HasMaxLength(400);
            e.Property(x => x.RemoteUrl).HasMaxLength(1000);
            e.Property(x => x.ErrorMessage).HasMaxLength(4000);
            e.Property(x => x.ImageAltText).HasMaxLength(1000);
            e.HasIndex(x => new { x.Status, x.NextAttemptUtc });
            e.HasOne(x => x.Image)
                .WithMany()
                .HasForeignKey(x => x.ImageId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => _tenant.RunAsSystem || x.UserId == _tenant.UserId);
        });

        builder.Entity<StoredImage>(e =>
        {
            e.Property(x => x.OriginalFileName).HasMaxLength(400);
            e.Property(x => x.StorageKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.StorageKey).IsUnique();
            e.HasQueryFilter(x => _tenant.RunAsSystem || x.UserId == _tenant.UserId);
        });

        builder.Entity<PublishLog>(e =>
        {
            e.Property(x => x.Platform).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Message).HasMaxLength(4000);
            e.HasIndex(x => new { x.PostId, x.StartedUtc });
            e.HasQueryFilter(x => _tenant.RunAsSystem || x.UserId == _tenant.UserId);
        });
    }
}
