namespace SocialShare.Core.Domain;

/// <summary>
/// The six platforms SocialShare can publish to. The numeric values double as the
/// canonical display order used everywhere in the UI.
/// </summary>
public enum SocialPlatform
{
    LinkedIn = 1,
    Bluesky = 2,
    Mastodon = 3,
    X = 4,
    Threads = 5,
    Instagram = 6
}

public enum Plan
{
    Free = 0,
    Pro = 1
}

public enum PostStatus
{
    Draft = 0,
    Scheduled = 1,
    Publishing = 2,
    Published = 3,
    PartiallyPublished = 4,
    Failed = 5,
    Canceled = 6
}

public enum TargetStatus
{
    Pending = 0,
    Publishing = 1,
    Published = 2,
    Failed = 3,
    Skipped = 4
}

public enum AccountStatus
{
    NotConfigured = 0,
    Configured = 1,
    Connected = 2,
    Error = 3
}

/// <summary>How a platform expects the user to authorize SocialShare.</summary>
public enum PlatformAuthKind
{
    /// <summary>Plain OAuth 2.0 authorization code flow.</summary>
    OAuth2 = 0,

    /// <summary>OAuth 2.0 authorization code flow with PKCE.</summary>
    OAuth2Pkce = 1,

    /// <summary>Credentials are exchanged directly for a session, no browser redirect.</summary>
    DirectCredentials = 2
}

public enum CredentialFieldKind
{
    Text = 0,
    Secret = 1,
    Url = 2
}
