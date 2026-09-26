using Microsoft.AspNetCore.Identity;

namespace SocialShare.Core.Domain;

/// <summary>
/// A tenant. Every user gets one of these on registration. Team features are not built yet,
/// but every owned row already carries an OrganizationId so adding them later is additive.
/// </summary>
public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Plan Plan { get; set; } = Plan.Free;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<ApplicationUser> Users { get; set; } = [];
}

public class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>IANA time zone id, for example America/Detroit.</summary>
    public string TimeZoneId { get; set; } = "America/Detroit";

    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { }

    public ApplicationRole(string name) : base(name) { }
}

/// <summary>
/// One row per user per platform. Everything secret lives in the two encrypted blobs:
/// <see cref="CredentialsCipher"/> holds what the user typed into the connect form
/// (client id, client secret, app password, instance url) and <see cref="TokensCipher"/>
/// holds whatever the platform handed back (access token, refresh token, session tokens).
/// </summary>
public class SocialAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }
    public SocialPlatform Platform { get; set; }

    /// <summary>Handle or profile name shown in the UI. Not secret.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Remote account identifier (member URN, DID, Threads user id). Not secret.</summary>
    public string? RemoteAccountId { get; set; }

    public string? CredentialsCipher { get; set; }
    public string? TokensCipher { get; set; }

    public DateTimeOffset? TokenExpiresUtc { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.NotConfigured;

    public DateTimeOffset? LastTestedUtc { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public string? LastError { get; set; }

    /// <summary>Per instance character limit, cached from the platform where it varies (Mastodon).</summary>
    public int? CharacterLimitOverride { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class Post
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>My own label for the post. Never sent to any platform.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional starting text that can be copied into the enabled platform bodies.</summary>
    public string? MasterBody { get; set; }

    public PostStatus Status { get; set; } = PostStatus.Draft;

    public DateTimeOffset? ScheduledUtc { get; set; }
    public DateTimeOffset? PublishedUtc { get; set; }

    /// <summary>Set when a worker or a request claims the post so nothing double publishes.</summary>
    public DateTimeOffset? ClaimedUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<PostTarget> Targets { get; set; } = [];
}

public class PostTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PostId { get; set; }
    public Post? Post { get; set; }

    public Guid UserId { get; set; }
    public SocialPlatform Platform { get; set; }

    public bool Enabled { get; set; }
    public string Body { get; set; } = string.Empty;

    public Guid? ImageId { get; set; }
    public StoredImage? Image { get; set; }
    public string? ImageAltText { get; set; }

    public TargetStatus Status { get; set; } = TargetStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }

    public string? RemotePostId { get; set; }
    public string? RemoteUrl { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? PublishedUtc { get; set; }
}

public class StoredImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>Random, unguessable key used both on disk and in the public URL.</summary>
    public string StorageKey { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;
    public long ByteSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One row per publish attempt per platform, so failures are readable after the fact.</summary>
public class PublishLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid PostId { get; set; }
    public Guid PostTargetId { get; set; }
    public SocialPlatform Platform { get; set; }

    public int AttemptNumber { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset CompletedUtc { get; set; }
    public bool Success { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? Message { get; set; }
}
