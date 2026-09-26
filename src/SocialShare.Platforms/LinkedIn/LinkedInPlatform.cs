using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Platforms.Shared;

namespace SocialShare.Platforms.LinkedIn;

/// <summary>
/// LinkedIn member posts. OAuth 2.0 authorization code, then the versioned Posts API. Images
/// are a two step affair: initialize an upload to get a URL and an image URN, PUT the bytes,
/// then reference the URN from the post.
///
/// Docs: https://learn.microsoft.com/en-us/linkedin/shared/authentication/authorization-code-flow
/// Docs: https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/posts-api
/// Docs: https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/images-api
/// Docs: https://learn.microsoft.com/en-us/linkedin/consumer/integrations/self-serve/sign-in-with-linkedin-v2
/// </summary>
public sealed class LinkedInPlatform(IHttpClientFactory httpClientFactory, ILogger<LinkedInPlatform> logger)
    : PlatformBase(httpClientFactory, logger)
{
    public const string ClientIdKey = "clientId";
    public const string ClientSecretKey = "clientSecret";

    private const string Scopes = "openid profile w_member_social";
    private const string ApiBase = "https://api.linkedin.com";

    /// <summary>
    /// The versioned APIs want a YYYYMM header. LinkedIn sunsets versions roughly a year out,
    /// so this is a constant to bump rather than something derived from the clock.
    /// </summary>
    private const string ApiVersion = "202609";

    public override SocialPlatform Platform => SocialPlatform.LinkedIn;

    public override PlatformCapabilities Capabilities { get; } = new()
    {
        Platform = SocialPlatform.LinkedIn,
        DisplayName = "LinkedIn",
        AuthKind = PlatformAuthKind.OAuth2,
        DefaultCharacterLimit = 3000,
        RecommendedImage = "1200 by 627 for the link style card, or 1200 by 1200 square. Under 5 MB.",
        RecommendedAspectRatio = 1.91,
        SetupDocAnchor = "linkedin",
        Summary = "Needs a LinkedIn developer app with the Share on LinkedIn and Sign In products added.",
        CredentialFields =
        [
            new CredentialField(ClientIdKey, "Client id",
                "From your LinkedIn developer app, on the Auth tab.", CredentialFieldKind.Text),
            new CredentialField(ClientSecretKey, "Client secret",
                "From the same Auth tab. Treat it like a password.", CredentialFieldKind.Secret)
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
                null, null, "Add your LinkedIn client id before connecting."));
        }

        var query = OAuthHelpers.BuildQuery(
        [
            new("response_type", "code"),
            new("client_id", clientId),
            new("redirect_uri", context.RedirectUri),
            new("state", context.State),
            new("scope", Scopes)
        ]);

        return Task.FromResult(new PlatformAuthorizationStart(
            new Uri($"https://www.linkedin.com/oauth/v2/authorization?{query}")));
    }

    public override async Task<PlatformConnectResult> CompleteAuthorizationAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        OAuthCallbackContext context,
        CancellationToken cancellationToken)
    {
        using var client = NewClient();
        var token = await ExchangeAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = context.Code,
            ["redirect_uri"] = context.RedirectUri,
            ["client_id"] = Require(credentials, ClientIdKey, "Client id"),
            ["client_secret"] = Require(credentials, ClientSecretKey, "Client secret")
        }, cancellationToken);

        if (token.Error is not null)
        {
            return new PlatformConnectResult(false, Error: token.Error);
        }

        var profile = await ReadProfileAsync(client, token.AccessToken!, cancellationToken);
        if (profile.Sub is null)
        {
            return new PlatformConnectResult(false,
                Error: profile.Error ?? "LinkedIn issued a token but would not return the member id.");
        }

        return new PlatformConnectResult(
            true,
            DisplayName: profile.Name,
            RemoteAccountId: $"urn:li:person:{profile.Sub}",
            Tokens: token.ToDictionary($"urn:li:person:{profile.Sub}"),
            TokenExpiresUtc: token.ExpiresUtc);
    }

    public override async Task<PlatformConnectResult?> RefreshAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        if (account.TokenExpiresUtc is { } expires && expires > DateTimeOffset.UtcNow.AddMinutes(10))
        {
            return null;
        }

        var refreshToken = Value(tokens, "refreshToken");
        if (refreshToken is null)
        {
            // Refresh tokens are only issued to approved apps. Without one the member has to
            // reconnect, and saying so is more useful than a generic 401 later.
            return account.TokenExpiresUtc is { } hardExpiry && hardExpiry <= DateTimeOffset.UtcNow
                ? new PlatformConnectResult(false,
                    Error: "The LinkedIn token has expired and no refresh token was issued. Connect LinkedIn again.")
                : null;
        }

        using var client = NewClient();
        var token = await ExchangeAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = Require(credentials, ClientIdKey, "Client id"),
            ["client_secret"] = Require(credentials, ClientSecretKey, "Client secret")
        }, cancellationToken);

        if (token.Error is not null)
        {
            return new PlatformConnectResult(false, Error: token.Error);
        }

        var memberUrn = Value(tokens, "memberUrn") ?? account.RemoteAccountId ?? string.Empty;
        return new PlatformConnectResult(
            true,
            RemoteAccountId: memberUrn,
            Tokens: token.ToDictionary(memberUrn, refreshToken),
            TokenExpiresUtc: token.ExpiresUtc);
    }

    public override async Task<PlatformTestResult> TestAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        var accessToken = Value(tokens, "accessToken");
        if (accessToken is null)
        {
            return new PlatformTestResult(false, "LinkedIn is not connected yet.");
        }

        using var client = NewClient();
        var profile = await ReadProfileAsync(client, accessToken, cancellationToken);

        return profile.Sub is null
            ? new PlatformTestResult(false, profile.Error ?? "LinkedIn rejected the stored token.")
            : new PlatformTestResult(true, $"Connected as {profile.Name}.", profile.Name);
    }

    public override async Task<PlatformPublishResult> PublishAsync(
        PlatformPublishRequest request,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        var accessToken = Value(tokens, "accessToken");
        var author = Value(tokens, "memberUrn") ?? request.Account.RemoteAccountId;

        if (accessToken is null || author is null)
        {
            return new PlatformPublishResult(false, Error: "LinkedIn is not connected. Connect it and retry.");
        }

        using var client = NewClient();

        object? content = null;
        if (request.Image is not null)
        {
            var upload = await UploadImageAsync(client, accessToken, author, request.Image, cancellationToken);
            if (upload.Error is not null)
            {
                return new PlatformPublishResult(false, Error: upload.Error, HttpStatusCode: upload.StatusCode);
            }

            content = new
            {
                media = new
                {
                    id = upload.ImageUrn,
                    altText = request.Image.AltText ?? string.Empty
                }
            };
        }

        var body = new Dictionary<string, object?>
        {
            ["author"] = author,
            ["commentary"] = request.Body,
            ["visibility"] = "PUBLIC",
            ["distribution"] = new
            {
                feedDistribution = "MAIN_FEED",
                targetEntities = Array.Empty<string>(),
                thirdPartyDistributionChannels = Array.Empty<string>()
            },
            ["lifecycleState"] = "PUBLISHED",
            ["isReshareDisabledByAuthor"] = false
        };

        if (content is not null)
        {
            body["content"] = content;
        }

        using var postRequest = NewApiRequest(HttpMethod.Post, $"{ApiBase}/rest/posts", accessToken);
        postRequest.Content = JsonContent.Create(body, options: Json);

        using var response = await client.SendAsync(postRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new PlatformPublishResult(false,
                Error: await DescribeFailureAsync(response, cancellationToken),
                HttpStatusCode: (int)response.StatusCode);
        }

        // The created post URN comes back in a header, not the body.
        var urn = response.Headers.TryGetValues("x-restli-id", out var ids) ? ids.FirstOrDefault() : null;

        return new PlatformPublishResult(
            true,
            RemotePostId: urn,
            RemoteUrl: urn is null ? null : $"https://www.linkedin.com/feed/update/{urn}/");
    }

    private HttpRequestMessage NewApiRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("LinkedIn-Version", ApiVersion);
        request.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");
        return request;
    }

    private sealed record ImageUpload(string? ImageUrn, string? Error, int? StatusCode);

    private async Task<ImageUpload> UploadImageAsync(
        HttpClient client,
        string accessToken,
        string ownerUrn,
        PublishImage image,
        CancellationToken cancellationToken)
    {
        using var initRequest = NewApiRequest(
            HttpMethod.Post, $"{ApiBase}/rest/images?action=initializeUpload", accessToken);
        initRequest.Content = JsonContent.Create(
            new { initializeUploadRequest = new { owner = ownerUrn } }, options: Json);

        using var initResponse = await client.SendAsync(initRequest, cancellationToken);
        if (!initResponse.IsSuccessStatusCode)
        {
            return new ImageUpload(null,
                $"LinkedIn would not start the image upload. {await DescribeFailureAsync(initResponse, cancellationToken)}",
                (int)initResponse.StatusCode);
        }

        var payload = await ReadJsonAsync(initResponse, cancellationToken);
        if (!payload.TryGetProperty("value", out var value))
        {
            return new ImageUpload(null, "LinkedIn returned an upload response with no value block.", null);
        }

        var uploadUrl = Str(value, "uploadUrl");
        var imageUrn = Str(value, "image");
        if (uploadUrl is null || imageUrn is null)
        {
            return new ImageUpload(null, "LinkedIn returned an upload response without a URL or an image URN.", null);
        }

        await using var stream = await image.OpenRead(cancellationToken);
        using var putContent = new StreamContent(stream);
        putContent.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);

        using var putRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = putContent };
        putRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var putResponse = await client.SendAsync(putRequest, cancellationToken);
        if (!putResponse.IsSuccessStatusCode)
        {
            return new ImageUpload(null,
                $"Uploading the image to LinkedIn failed. {await DescribeFailureAsync(putResponse, cancellationToken)}",
                (int)putResponse.StatusCode);
        }

        return new ImageUpload(imageUrn, null, null);
    }

    private sealed record TokenResponse(string? AccessToken, string? RefreshToken, DateTimeOffset? ExpiresUtc, string? Error)
    {
        public Dictionary<string, string> ToDictionary(string memberUrn, string? fallbackRefreshToken = null)
        {
            var result = new Dictionary<string, string>
            {
                ["accessToken"] = AccessToken ?? string.Empty,
                ["memberUrn"] = memberUrn
            };

            var refresh = RefreshToken ?? fallbackRefreshToken;
            if (refresh is not null)
            {
                result["refreshToken"] = refresh;
            }

            return result;
        }
    }

    private async Task<TokenResponse> ExchangeAsync(
        HttpClient client,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(
            "https://www.linkedin.com/oauth/v2/accessToken",
            new FormUrlEncodedContent(form),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new TokenResponse(null, null, null,
                $"LinkedIn would not issue a token. {await DescribeFailureAsync(response, cancellationToken)}");
        }

        var payload = await ReadJsonAsync(response, cancellationToken);
        var accessToken = Str(payload, "access_token");
        if (accessToken is null)
        {
            return new TokenResponse(null, null, null, "LinkedIn returned a token response with no access token.");
        }

        DateTimeOffset? expires = payload.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.UtcNow.AddSeconds(e.GetInt32())
            : null;

        return new TokenResponse(accessToken, Str(payload, "refresh_token"), expires, null);
    }

    private sealed record Profile(string? Sub, string? Name, string? Error);

    private async Task<Profile> ReadProfileAsync(HttpClient client, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/v2/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new Profile(null, null, await DescribeFailureAsync(response, cancellationToken));
        }

        var payload = await ReadJsonAsync(response, cancellationToken);
        return new Profile(Str(payload, "sub"), Str(payload, "name"), null);
    }
}
