using SocialShare.Core.Domain;

namespace SocialShare.Core.Platforms;

/// <summary>
/// One field a user has to fill in on the Connected accounts page before a platform
/// can be connected. The UI renders these generically so no page knows platform names.
/// </summary>
public sealed record CredentialField(
    string Key,
    string Label,
    string Help,
    CredentialFieldKind Kind = CredentialFieldKind.Text,
    bool Required = true,
    string? Placeholder = null,
    string? DefaultValue = null);

/// <summary>Everything the UI and the publisher need to know about a platform without naming it.</summary>
public sealed record PlatformCapabilities
{
    public required SocialPlatform Platform { get; init; }
    public required string DisplayName { get; init; }
    public required PlatformAuthKind AuthKind { get; init; }
    public required int DefaultCharacterLimit { get; init; }

    /// <summary>True when the platform refuses to publish without an image.</summary>
    public bool ImageRequired { get; init; }

    public bool ImageSupported { get; init; } = true;

    /// <summary>True when the image has to be fetched by the platform from a public URL.</summary>
    public bool RequiresPublicImageUrl { get; init; }

    /// <summary>True when posting needs a paid developer tier, so the UI can warn up front.</summary>
    public bool RequiresPaidTier { get; init; }

    /// <summary>Human readable image guidance shown next to the upload control.</summary>
    public required string RecommendedImage { get; init; }

    /// <summary>Preferred width divided by height. Used to warn when an upload is far off.</summary>
    public double RecommendedAspectRatio { get; init; } = 1.91;

    /// <summary>How far off the recommended ratio an image can be before the UI warns.</summary>
    public double AspectRatioTolerance { get; init; } = 0.6;

    public required IReadOnlyList<CredentialField> CredentialFields { get; init; }

    /// <summary>Anchor in docs/platform-setup.md for the per platform instructions.</summary>
    public required string SetupDocAnchor { get; init; }

    public required string Summary { get; init; }
}

public sealed record PlatformConnectResult(
    bool Success,
    string? DisplayName = null,
    string? RemoteAccountId = null,
    IReadOnlyDictionary<string, string>? Tokens = null,
    DateTimeOffset? TokenExpiresUtc = null,
    int? CharacterLimit = null,
    string? Error = null);

public sealed record PlatformTestResult(bool Success, string Message, string? DisplayName = null);

public sealed record PlatformPublishResult(
    bool Success,
    string? RemotePostId = null,
    string? RemoteUrl = null,
    string? Error = null,
    int? HttpStatusCode = null);

/// <summary>An image that is ready to publish, in both of the shapes the platforms ask for.</summary>
public sealed record PublishImage(
    Func<CancellationToken, Task<Stream>> OpenRead,
    string ContentType,
    long ByteSize,
    int Width,
    int Height,
    string PublicUrl,
    string? AltText);

public sealed record PlatformPublishRequest(
    SocialAccount Account,
    string Body,
    PublishImage? Image);

/// <summary>Everything a platform needs to start or finish a browser based authorization.</summary>
public sealed record OAuthStartContext(string RedirectUri, string State, string? CodeChallenge);

public sealed record OAuthCallbackContext(string RedirectUri, string Code, string? CodeVerifier);
