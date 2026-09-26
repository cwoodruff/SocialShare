using System.Text.Json;
using Microsoft.Extensions.Logging;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Platforms.Shared;

namespace SocialShare.Platforms.Instagram;

/// <summary>
/// Instagram through the Graph API with Facebook Login. The account has to be a Business or
/// Creator account linked to a Facebook Page, so connecting walks the Page list to find the
/// linked Instagram account id. Publishing is container then publish, and an image is mandatory
/// because the API has no text only post.
///
/// Docs: https://developers.facebook.com/docs/instagram-platform/content-publishing
/// Docs: https://developers.facebook.com/docs/instagram-platform/instagram-graph-api/get-started
/// Docs: https://developers.facebook.com/docs/facebook-login/guides/access-tokens/get-long-lived
/// </summary>
public sealed class InstagramPlatform(IHttpClientFactory httpClientFactory, ILogger<InstagramPlatform> logger)
    : PlatformBase(httpClientFactory, logger)
{
    public const string ClientIdKey = "clientId";
    public const string ClientSecretKey = "clientSecret";

    private const string GraphBase = "https://graph.facebook.com";
    private const string GraphVersion = "v23.0";

    private const string Scopes =
        "instagram_basic,instagram_content_publish,pages_show_list,pages_read_engagement,business_management";

    public override SocialPlatform Platform => SocialPlatform.Instagram;

    public override PlatformCapabilities Capabilities { get; } = new()
    {
        Platform = SocialPlatform.Instagram,
        DisplayName = "Instagram",
        AuthKind = PlatformAuthKind.OAuth2,
        DefaultCharacterLimit = 2200,
        ImageRequired = true,
        RequiresPublicImageUrl = true,
        RecommendedImage = "Square 1080 by 1080, or portrait 1080 by 1350. Aspect ratio must sit between 4:5 and 1.91:1.",
        RecommendedAspectRatio = 1.0,
        AspectRatioTolerance = 0.95,
        SetupDocAnchor = "instagram",
        Summary = "Needs a Meta app, a Business or Creator account and a linked Facebook Page. Every post needs an image.",
        CredentialFields =
        [
            new CredentialField(ClientIdKey, "Meta app id",
                "From your Meta app dashboard, under App settings, Basic.", CredentialFieldKind.Text),
            new CredentialField(ClientSecretKey, "Meta app secret",
                "From the same page. Click Show to reveal it.", CredentialFieldKind.Secret)
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
                null, null, "Add your Meta app id before connecting."));
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
            new Uri($"https://www.facebook.com/{GraphVersion}/dialog/oauth?{query}")));
    }

    public override async Task<PlatformConnectResult> CompleteAuthorizationAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        OAuthCallbackContext context,
        CancellationToken cancellationToken)
    {
        var clientId = Require(credentials, ClientIdKey, "Meta app id");
        var clientSecret = Require(credentials, ClientSecretKey, "Meta app secret");

        using var client = NewClient();

        var exchangeQuery = OAuthHelpers.BuildQuery(
        [
            new("client_id", clientId),
            new("client_secret", clientSecret),
            new("redirect_uri", context.RedirectUri),
            new("code", context.Code)
        ]);

        using var shortResponse = await client.GetAsync(
            $"{GraphBase}/{GraphVersion}/oauth/access_token?{exchangeQuery}", cancellationToken);

        if (!shortResponse.IsSuccessStatusCode)
        {
            return new PlatformConnectResult(false,
                Error: $"Meta would not exchange the code. {await DescribeFailureAsync(shortResponse, cancellationToken)}");
        }

        var shortToken = Str(await ReadJsonAsync(shortResponse, cancellationToken), "access_token");
        if (shortToken is null)
        {
            return new PlatformConnectResult(false, Error: "Meta returned a token response with no access token.");
        }

        var longQuery = OAuthHelpers.BuildQuery(
        [
            new("grant_type", "fb_exchange_token"),
            new("client_id", clientId),
            new("client_secret", clientSecret),
            new("fb_exchange_token", shortToken)
        ]);

        using var longResponse = await client.GetAsync(
            $"{GraphBase}/{GraphVersion}/oauth/access_token?{longQuery}", cancellationToken);

        if (!longResponse.IsSuccessStatusCode)
        {
            return new PlatformConnectResult(false,
                Error: $"Meta would not issue a long lived token. {await DescribeFailureAsync(longResponse, cancellationToken)}");
        }

        var longPayload = await ReadJsonAsync(longResponse, cancellationToken);
        var longToken = Str(longPayload, "access_token") ?? shortToken;

        var linked = await FindInstagramAccountAsync(client, longToken, cancellationToken);
        if (linked.AccountId is null)
        {
            return new PlatformConnectResult(false, Error: linked.Error);
        }

        return new PlatformConnectResult(
            true,
            DisplayName: linked.Username is null ? null : "@" + linked.Username,
            RemoteAccountId: linked.AccountId,
            Tokens: new Dictionary<string, string>
            {
                ["accessToken"] = longToken,
                ["igUserId"] = linked.AccountId
            },
            TokenExpiresUtc: longPayload.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.UtcNow.AddSeconds(e.GetInt64())
                : DateTimeOffset.UtcNow.AddDays(60));
    }

    public override async Task<PlatformTestResult> TestAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        var accessToken = Value(tokens, "accessToken");
        var igUserId = Value(tokens, "igUserId") ?? account.RemoteAccountId;

        if (accessToken is null || igUserId is null)
        {
            return new PlatformTestResult(false, "Instagram is not connected yet.");
        }

        using var client = NewClient();
        var query = OAuthHelpers.BuildQuery(
        [
            new("fields", "id,username"),
            new("access_token", accessToken)
        ]);

        using var response = await client.GetAsync($"{GraphBase}/{GraphVersion}/{igUserId}?{query}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new PlatformTestResult(false, await DescribeFailureAsync(response, cancellationToken));
        }

        var username = Str(await ReadJsonAsync(response, cancellationToken), "username");
        return new PlatformTestResult(true, $"Connected as @{username}.", username is null ? null : "@" + username);
    }

    public override async Task<PlatformPublishResult> PublishAsync(
        PlatformPublishRequest request,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        var accessToken = Value(tokens, "accessToken");
        var igUserId = Value(tokens, "igUserId") ?? request.Account.RemoteAccountId;

        if (accessToken is null || igUserId is null)
        {
            return new PlatformPublishResult(false, Error: "Instagram is not connected. Connect it and retry.");
        }

        if (request.Image is null)
        {
            return new PlatformPublishResult(false,
                Error: "Instagram has no text only post. Attach an image and retry.");
        }

        using var client = NewClient();

        using var containerResponse = await client.PostAsync(
            $"{GraphBase}/{GraphVersion}/{igUserId}/media",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["image_url"] = request.Image.PublicUrl,
                ["caption"] = request.Body,
                ["access_token"] = accessToken
            }),
            cancellationToken);

        if (!containerResponse.IsSuccessStatusCode)
        {
            return new PlatformPublishResult(false,
                Error: await DescribeFailureAsync(containerResponse, cancellationToken),
                HttpStatusCode: (int)containerResponse.StatusCode);
        }

        var creationId = Str(await ReadJsonAsync(containerResponse, cancellationToken), "id");
        if (creationId is null)
        {
            return new PlatformPublishResult(false, Error: "Instagram created a container but returned no id.");
        }

        var ready = await WaitForContainerAsync(client, creationId, accessToken, cancellationToken);
        if (ready is not null)
        {
            return new PlatformPublishResult(false, Error: ready);
        }

        using var publishResponse = await client.PostAsync(
            $"{GraphBase}/{GraphVersion}/{igUserId}/media_publish",
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

        var mediaId = Str(await ReadJsonAsync(publishResponse, cancellationToken), "id");
        var permalink = mediaId is null ? null : await ReadPermalinkAsync(client, mediaId, accessToken, cancellationToken);

        return new PlatformPublishResult(true, mediaId, permalink);
    }

    /// <summary>
    /// Meta downloads the image before the container is publishable. Returns null once it is
    /// ready, or a message describing why it never got there.
    /// </summary>
    private async Task<string?> WaitForContainerAsync(
        HttpClient client, string creationId, string accessToken, CancellationToken cancellationToken)
    {
        var query = OAuthHelpers.BuildQuery(
        [
            new("fields", "status_code,status"),
            new("access_token", accessToken)
        ]);

        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

            using var response = await client.GetAsync(
                $"{GraphBase}/{GraphVersion}/{creationId}?{query}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var payload = await ReadJsonAsync(response, cancellationToken);
            switch (Str(payload, "status_code"))
            {
                case "FINISHED":
                    return null;
                case "ERROR":
                    return $"Instagram could not process the image. {Str(payload, "status") ?? "No detail given."}";
            }
        }

        return "Instagram was still processing the image after 36 seconds. Retry in a minute.";
    }

    private sealed record LinkedAccount(string? AccountId, string? Username, string? Error);

    /// <summary>
    /// Walks the Pages the token can see and returns the first one with a linked Instagram
    /// Business account. Anything else is a setup problem worth explaining clearly.
    /// </summary>
    private async Task<LinkedAccount> FindInstagramAccountAsync(
        HttpClient client, string accessToken, CancellationToken cancellationToken)
    {
        var query = OAuthHelpers.BuildQuery(
        [
            new("fields", "id,name,instagram_business_account{id,username}"),
            new("access_token", accessToken)
        ]);

        using var response = await client.GetAsync($"{GraphBase}/{GraphVersion}/me/accounts?{query}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new LinkedAccount(null, null,
                $"Meta would not list your Pages. {await DescribeFailureAsync(response, cancellationToken)}");
        }

        var payload = await ReadJsonAsync(response, cancellationToken);
        if (!payload.TryGetProperty("data", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            return new LinkedAccount(null, null, "Meta returned no Pages for this account.");
        }

        foreach (var page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("instagram_business_account", out var ig) || ig.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = Str(ig, "id");
            if (id is not null)
            {
                return new LinkedAccount(id, Str(ig, "username"), null);
            }
        }

        return new LinkedAccount(null, null,
            "None of your Facebook Pages has a linked Instagram Business or Creator account. "
            + "Link one in the Page settings and connect again.");
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

            using var response = await client.GetAsync(
                $"{GraphBase}/{GraphVersion}/{mediaId}?{query}", cancellationToken);

            return response.IsSuccessStatusCode
                ? Str(await ReadJsonAsync(response, cancellationToken), "permalink")
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Logger.LogDebug(ex, "Could not read the Instagram permalink for {MediaId}.", mediaId);
            return null;
        }
    }
}
