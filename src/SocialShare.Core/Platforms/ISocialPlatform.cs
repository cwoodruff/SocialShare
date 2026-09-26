using SocialShare.Core.Domain;

namespace SocialShare.Core.Platforms;

/// <summary>
/// The whole surface the app uses to talk to a social network. Nothing outside the
/// implementations and the setup documentation mentions a platform by name.
/// </summary>
public interface ISocialPlatform
{
    SocialPlatform Platform { get; }

    PlatformCapabilities Capabilities { get; }

    /// <summary>
    /// Builds the URL the browser is sent to for authorization. Only called when
    /// <see cref="PlatformCapabilities.AuthKind"/> is one of the OAuth kinds.
    /// </summary>
    Uri BuildAuthorizationUrl(SocialAccount account, IReadOnlyDictionary<string, string> credentials, OAuthStartContext context);

    /// <summary>Exchanges an authorization code for tokens.</summary>
    Task<PlatformConnectResult> CompleteAuthorizationAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        OAuthCallbackContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Connects without a browser redirect. Only used when
    /// <see cref="PlatformAuthKind.DirectCredentials"/> applies.
    /// </summary>
    Task<PlatformConnectResult> ConnectDirectAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken);

    /// <summary>
    /// Refreshes tokens when they are close to expiry. Returns null when nothing changed.
    /// </summary>
    Task<PlatformConnectResult?> RefreshAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken);

    /// <summary>A cheap read only call that proves the stored tokens still work.</summary>
    Task<PlatformTestResult> TestAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken);

    /// <summary>Publishes one body, with an optional image, to the platform.</summary>
    Task<PlatformPublishResult> PublishAsync(
        PlatformPublishRequest request,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken);

    /// <summary>Best effort revoke. Deleting the local row is what actually disconnects.</summary>
    Task DisconnectAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken);
}

/// <summary>Finds the implementation for a platform. Keeps DI keyed lookups out of the pages.</summary>
public interface ISocialPlatformRegistry
{
    ISocialPlatform Get(SocialPlatform platform);

    IReadOnlyList<ISocialPlatform> All { get; }
}
