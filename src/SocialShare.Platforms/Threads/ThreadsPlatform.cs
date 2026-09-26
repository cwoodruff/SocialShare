using System.Text.Json;
using Microsoft.Extensions.Logging;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Platforms.Shared;

namespace SocialShare.Platforms.Threads;

/// <summary>
/// Threads through a Meta developer app. Publishing is two calls: create a media container,
/// then publish it. Images are not uploaded, they are fetched by Meta from a public URL, which
/// is why SocialShare serves uploads from an unguessable public address.
///
/// Docs: https://developers.facebook.com/docs/threads/get-started
/// Docs: https://developers.facebook.com/docs/threads/posts
/// Docs: https://developers.facebook.com/docs/threads/get-started/long-lived-tokens
/// </summary>
public sealed class ThreadsPlatform(IHttpClientFactory httpClientFactory, ILogger<ThreadsPlatform> logger)
    : PlatformBase(httpClientFactory, logger)
{
    public const string ClientIdKey = "clientId";
    public const string ClientSecretKey = "clientSecret";

    private const string GraphBase = "https://graph.threads.net";
    private const string Scopes = "threads_basic,threads_content_publish";

    public override SocialPlatform Platform => SocialPlatform.Threads;

    public override PlatformCapabilities Capabilities { get; } = new()
    {
        Platform = SocialPlatform.Threads,
        DisplayName = "Threads",
        AuthKind = PlatformAuthKind.OAuth2,
        DefaultCharacterLimit = 500,
        RequiresPublicImageUrl = true,
        RecommendedImage = "Aspect ratios between 4:5 and 1.91:1. 1080 wide is plenty. Under 8 MB.",
        RecommendedAspectRatio = 1.0,
        AspectRatioTolerance = 0.95,
        SetupDocAnchor = "threads",
        Summary = "Needs a Meta developer app with the Threads API product added.",
        CredentialFields =
        [
            new CredentialField(ClientIdKey, "Threads app id",
                "From your Meta app, under Threads API, Settings. Sometimes labelled the Threads app id.",
                CredentialFieldKind.Text),
            new CredentialField(ClientSecretKey, "Threads app secret",
                "From the same settings page.", CredentialFieldKind.Secret)
        ]
    };

    public override Task<PlatformAuthorizationStart> StartAuthorizationAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        OAuthStartContext context,
        CancellationToken cancellationToken)
    {
        var clientId = Value(credentials, ClientIdKey);
        if (clientId is null)
        {
            return Task.FromResult(new PlatformAuthorizationStart(
                null, null, "Add your Threads app id before connecting."));
        }

        var query = OAuthHelpers.BuildQuery(
        [
            new("client_id", clientId),
            new("redirect_uri", context.RedirectUri),
            new("scope", Scopes),
            new("response_type", "code"),
            new("state", context.State)
        ]);

        return Task.FromResult(new PlatformAuthorizationStart(
            new Uri($"https://threads.net/oauth/authorize?{query}")));
    }

    public override async Task<PlatformConnectResult> CompleteAuthorizationAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        OAuthCallbackContext context,
        CancellationToken cancellationToken)
    {
        var clientId = Require(credentials, ClientIdKey, "Threads app id");
        var clientSecret = Require(credentials, ClientSecretKey, "Threads app secret");

        using var client = NewClient();

        using var shortResponse = await client.PostAsync(
            $"{GraphBase}/oauth/access_token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = context.RedirectUri,
                ["code"] = context.Code
            }),
            cancellationToken);

        if (!shortResponse.IsSuccessStatusCode)
        {
            return new PlatformConnectResult(false,
                Error: $"Threads would not exchange the code. {await DescribeFailureAsync(shortResponse, cancellationToken)}");
        }

        var shortPayload = await ReadJsonAsync(shortResponse, cancellationToken);
        var shortToken = Str(shortPayload, "access_token");
        var userId = Str(shortPayload, "user_id")
            ?? (shortPayload.TryGetProperty("user_id", out var raw) && raw.ValueKind == JsonValueKind.Number
                ? raw.GetInt64().ToString()
                : null);

        if (shortToken is null || userId is null)
        {
            return new PlatformConnectResult(false, Error: "Threads returned a token response without a token or user id.");
        }

        // Short lived tokens last an hour. Swap immediately for the 60 day one.
        var longQuery = OAuthHelpers.BuildQuery(
        [
            new("grant_type", "th_exchange_token"),
            new("client_secret", clientSecret),
            new("access_token", shortToken)
        ]);

        using var longResponse = await client.GetAsync($"{GraphBase}/access_token?{longQuery}", cancellationToken);
        if (!longResponse.IsSuccessStatusCode)
        {
            return new PlatformConnectResult(false,
                Error: $"Threads would not issue a long lived token. {await DescribeFailureAsync(longResponse, cancellationToken)}");
        }

        var longPayload = await ReadJsonAsync(longResponse, cancellationToken);
        var longToken = Str(longPayload, "access_token") ?? shortToken;
        var expires = ExpiresFrom(longPayload);

        var profile = await ReadProfileAsync(client, userId, longToken, cancellationToken);

        return new PlatformConnectResult(
            true,
            DisplayName: profile is null ? null : "@" + profile,
            RemoteAccountId: userId,
            Tokens: new Dictionary<string, string>
            {
                ["accessToken"] = longToken,
                ["userId"] = userId
            },
            TokenExpiresUtc: expires);
    }

    public override async Task<PlatformConnectResult?> RefreshAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        // Long lived tokens are refreshable once they are 24 hours old and before they expire.
        if (account.TokenExpiresUtc is not { } expires || expires > DateTimeOffset.UtcNow.AddDays(7))
        {
            return null;
        }

        var accessToken = Value(tokens, "accessToken");
        if (accessToken is null)
        {
            return new PlatformConnectResult(false, Error: "There is no Threads token to refresh. Connect Threads again.");
        }

        using var client = NewClient();
        var query = OAuthHelpers.BuildQuery(
        [
            new("grant_type", "th_refresh_token"),
            new("access_token", accessToken)
        ]);

        using var response = await client.GetAsync($"{GraphBase}/refresh_access_token?{query}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new PlatformConnectResult(false,
                Error: $"Refreshing the Threads token failed. {await DescribeFailureAsync(response, cancellationToken)}");
        }

        var payload = await ReadJsonAsync(response, cancellationToken);
        var refreshed = Str(payload, "access_token") ?? accessToken;

        return new PlatformConnectResult(
            true,
            RemoteAccountId: account.RemoteAccountId,
            Tokens: new Dictionary<string, string>
            {
                ["accessToken"] = refreshed,
                ["userId"] = Value(tokens, "userId") ?? account.RemoteAccountId ?? string.Empty
            },
            TokenExpiresUtc: ExpiresFrom(payload));
    }

    public override async Task<PlatformTestResult> TestAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        var accessToken = Value(tokens, "accessToken");
        var userId = Value(tokens, "userId") ?? account.RemoteAccountId;

        if (accessToken is null || userId is null)
        {
            return new PlatformTestResult(false, "Threads is not connected yet.");
        }

        using var client = NewClient();
        var username = await ReadProfileAsync(client, userId, accessToken, cancellationToken);

        return username is null
            ? new PlatformTestResult(false, "Threads rejected the stored token.")
            : new PlatformTestResult(true, $"Connected as @{username}.", "@" + username);
    }

    public override async Task<PlatformPublishResult> PublishAsync(
        PlatformPublishRequest request,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        var accessToken = Value(tokens, "accessToken");
        var userId = Value(tokens, "userId") ?? request.Account.RemoteAccountId;

        if (accessToken is null || userId is null)
        {
            return new PlatformPublishResult(false, Error: "Threads is not connected. Connect it and retry.");
        }

        using var client = NewClient();

        var form = new Dictionary<string, string>
        {
            ["media_type"] = request.Image is null ? "TEXT" : "IMAGE",
            ["text"] = request.Body,
            ["access_token"] = accessToken
        };

        if (request.Image is not null)
        {
            form["image_url"] = request.Image.PublicUrl;
            if (!string.IsNullOrWhiteSpace(request.Image.AltText))
            {
                form["alt_text"] = request.Image.AltText;
            }
        }

        using var containerResponse = await client.PostAsync(
            $"{GraphBase}/v1.0/{userId}/threads", new FormUrlEncodedContent(form), cancellationToken);

        if (!containerResponse.IsSuccessStatusCode)
        {
            return new PlatformPublishResult(false,
                Error: await DescribeFailureAsync(containerResponse, cancellationToken),
                HttpStatusCode: (int)containerResponse.StatusCode);
        }

        var container = await ReadJsonAsync(containerResponse, cancellationToken);
        var creationId = Str(container, "id");
        if (creationId is null)
        {
            return new PlatformPublishResult(false, Error: "Threads created a container but returned no id.");
        }

        if (request.Image is not null)
        {
            // Meta fetches the image itself, so the container is not publishable immediately.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        using var publishResponse = await client.PostAsync(
            $"{GraphBase}/v1.0/{userId}/threads_publish",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["creation_id"] = creationId,
                ["access_token"] = accessToken
            }),
            cancellationToken);

        if (!publishResponse.IsSuccessStatusCode)
        {
            return new PlatformPublishResult(false,
                Error: await DescribeFailureAsync(publishResponse, cancellationToken),
                HttpStatusCode: (int)publishResponse.StatusCode);
        }

        var published = await ReadJsonAsync(publishResponse, cancellationToken);
        var mediaId = Str(published, "id");
        var permalink = mediaId is null ? null : await ReadPermalinkAsync(client, mediaId, accessToken, cancellationToken);

        return new PlatformPublishResult(true, mediaId, permalink);
    }

    private static DateTimeOffset? ExpiresFrom(JsonElement payload) =>
        payload.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.UtcNow.AddSeconds(e.GetInt64())
            : null;

    private async Task<string?> ReadProfileAsync(
        HttpClient client, string userId, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var query = OAuthHelpers.BuildQuery(
            [
                new("fields", "id,username"),
                new("access_token", accessToken)
            ]);

            using var response = await client.GetAsync($"{GraphBase}/v1.0/{userId}?{query}", cancellationToken);
            return response.IsSuccessStatusCode
                ? Str(await ReadJsonAsync(response, cancellationToken), "username")
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Logger.LogDebug(ex, "Could not read the Threads profile for {UserId}.", userId);
            return null;
        }
    }

    private async Task<string?> ReadPermalinkAsync(
        HttpClient client, string mediaId, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var query = OAuthHelpers.BuildQuery(
            [
                new("fields", "permalink"),
                new("access_token", accessToken)
            ]);

            using var response = await client.GetAsync($"{GraphBase}/v1.0/{mediaId}?{query}", cancellationToken);
            return response.IsSuccessStatusCode
                ? Str(await ReadJsonAsync(response, cancellationToken), "permalink")
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Logger.LogDebug(ex, "Could not read the Threads permalink for {MediaId}.", mediaId);
            return null;
        }
    }
}
