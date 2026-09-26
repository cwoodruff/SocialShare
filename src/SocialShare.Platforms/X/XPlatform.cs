using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Platforms.Shared;

namespace SocialShare.Platforms.X;

/// <summary>
/// X. OAuth 2.0 authorization code with PKCE, then the v2 endpoints. Posting needs a paid
/// developer tier: the free tier is read only for most apps, so a connect can succeed and the
/// publish still come back as 403. The UI says so up front and the error is surfaced as is.
///
/// Docs: https://docs.x.com/resources/fundamentals/authentication/oauth-2-0/authorization-code
/// Docs: https://docs.x.com/x-api/posts/creation-of-a-post
/// Docs: https://docs.x.com/x-api/media/quickstart/media-upload-chunked
/// </summary>
public sealed class XPlatform(IHttpClientFactory httpClientFactory, ILogger<XPlatform> logger)
    : PlatformBase(httpClientFactory, logger)
{
    public const string ClientIdKey = "clientId";
    public const string ClientSecretKey = "clientSecret";

    private const string Scopes = "tweet.read tweet.write users.read offline.access media.write";
    private const string ApiBase = "https://api.x.com";

    /// <summary>Chunked upload wants segments of 5 MB or less.</summary>
    private const int ChunkBytes = 4 * 1024 * 1024;

    public override SocialPlatform Platform => SocialPlatform.X;

    public override PlatformCapabilities Capabilities { get; } = new()
    {
        Platform = SocialPlatform.X,
        DisplayName = "X",
        AuthKind = PlatformAuthKind.OAuth2Pkce,
        DefaultCharacterLimit = 280,
        RequiresPaidTier = true,
        RecommendedImage = "1600 by 900 for landscape. Under 5 MB. Anything wider than 2:1 gets cropped in the timeline.",
        RecommendedAspectRatio = 1.78,
        SetupDocAnchor = "x",
        Summary = "Needs an X developer app on a paid tier. Free projects can connect but cannot post.",
        CredentialFields =
        [
            new CredentialField(ClientIdKey, "Client id",
                "From the X developer portal, under your app's User authentication settings.",
                CredentialFieldKind.Text),
            new CredentialField(ClientSecretKey, "Client secret",
                "Only needed for confidential clients. Leave it blank if your app is public.",
                CredentialFieldKind.Secret, Required: false)
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
                null, null, "Add your X client id before connecting."));
        }

        if (context.CodeChallenge is null)
        {
            return Task.FromResult(new PlatformAuthorizationStart(
                null, null, "X needs a PKCE challenge and none was generated."));
        }

        var query = OAuthHelpers.BuildQuery(
        [
            new("response_type", "code"),
            new("client_id", clientId),
            new("redirect_uri", context.RedirectUri),
            new("scope", Scopes),
            new("state", context.State),
            new("code_challenge", context.CodeChallenge),
            new("code_challenge_method", "S256")
        ]);

        return Task.FromResult(new PlatformAuthorizationStart(
            new Uri($"https://x.com/i/oauth2/authorize?{query}")));
    }

    public override async Task<PlatformConnectResult> CompleteAuthorizationAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        OAuthCallbackContext context,
        CancellationToken cancellationToken)
    {
        if (context.CodeVerifier is null)
        {
            return new PlatformConnectResult(false, Error: "The PKCE verifier went missing between the two steps.");
        }

        using var client = NewClient();
        var token = await ExchangeAsync(client, credentials, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = context.Code,
            ["redirect_uri"] = context.RedirectUri,
            ["client_id"] = Require(credentials, ClientIdKey, "Client id"),
            ["code_verifier"] = context.CodeVerifier
        }, cancellationToken);

        if (token.Error is not null)
        {
            return new PlatformConnectResult(false, Error: token.Error);
        }

        var me = await ReadMeAsync(client, token.AccessToken!, cancellationToken);
        if (me.Id is null)
        {
            return new PlatformConnectResult(false,
                Error: me.Error ?? "X issued a token but would not return the account.");
        }

        return new PlatformConnectResult(
            true,
            DisplayName: me.Username is null ? null : "@" + me.Username,
            RemoteAccountId: me.Id,
            Tokens: token.ToDictionary(me.Username),
            TokenExpiresUtc: token.ExpiresUtc);
    }

    public override async Task<PlatformConnectResult?> RefreshAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        if (account.TokenExpiresUtc is { } expires && expires > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return null;
        }

        var refreshToken = Value(tokens, "refreshToken");
        if (refreshToken is null)
        {
            return new PlatformConnectResult(false,
                Error: "The X token expired and there is no refresh token. Connect X again.");
        }

        using var client = NewClient();
        var token = await ExchangeAsync(client, credentials, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = Require(credentials, ClientIdKey, "Client id")
        }, cancellationToken);

        if (token.Error is not null)
        {
            return new PlatformConnectResult(false, Error: token.Error);
        }

        return new PlatformConnectResult(
            true,
            RemoteAccountId: account.RemoteAccountId,
            Tokens: token.ToDictionary(Value(tokens, "username"), refreshToken),
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
            return new PlatformTestResult(false, "X is not connected yet.");
        }

        using var client = NewClient();
        var me = await ReadMeAsync(client, accessToken, cancellationToken);

        return me.Id is null
            ? new PlatformTestResult(false, me.Error ?? "X rejected the stored token.")
            : new PlatformTestResult(true,
                $"Connected as @{me.Username}. Posting still needs a paid tier on the developer account.",
                "@" + me.Username);
    }

    public override async Task<PlatformPublishResult> PublishAsync(
        PlatformPublishRequest request,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        var accessToken = Value(tokens, "accessToken");
        if (accessToken is null)
        {
            return new PlatformPublishResult(false, Error: "X is not connected. Connect it and retry.");
        }

        using var client = NewClient();

        string? mediaId = null;
        if (request.Image is not null)
        {
            var upload = await UploadMediaAsync(client, accessToken, request.Image, cancellationToken);
            if (upload.Error is not null)
            {
                return new PlatformPublishResult(false, Error: upload.Error, HttpStatusCode: upload.StatusCode);
            }

            mediaId = upload.MediaId;
        }

        var body = new Dictionary<string, object?> { ["text"] = request.Body };
        if (mediaId is not null)
        {
            body["media"] = new { media_ids = new[] { mediaId } };
        }

        using var postRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/2/tweets")
        {
            Content = JsonContent.Create(body, options: Json)
        };
        postRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(postRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await DescribeFailureAsync(response, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden)
            {
                detail += " A 403 here usually means the developer project is on the free tier, which cannot post.";
            }

            return new PlatformPublishResult(false, Error: detail, HttpStatusCode: (int)response.StatusCode);
        }

        var payload = await ReadJsonAsync(response, cancellationToken);
        var id = payload.TryGetProperty("data", out var data) ? Str(data, "id") : null;
        var username = Value(tokens, "username") ?? "i";

        return new PlatformPublishResult(
            true,
            RemotePostId: id,
            RemoteUrl: id is null ? null : $"https://x.com/{username}/status/{id}");
    }

    private sealed record MediaUpload(string? MediaId, string? Error, int? StatusCode);

    /// <summary>
    /// Chunked upload: initialize, append every segment, finalize, then poll if X says it is
    /// still processing. Docs: https://docs.x.com/x-api/media/quickstart/media-upload-chunked
    /// </summary>
    private async Task<MediaUpload> UploadMediaAsync(
        HttpClient client,
        string accessToken,
        PublishImage image,
        CancellationToken cancellationToken)
    {
        using var initRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/2/media/upload/initialize")
        {
            Content = JsonContent.Create(new
            {
                media_type = image.ContentType,
                total_bytes = image.ByteSize,
                media_category = "tweet_image"
            }, options: Json)
        };
        initRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var initResponse = await client.SendAsync(initRequest, cancellationToken);
        if (!initResponse.IsSuccessStatusCode)
        {
            return new MediaUpload(null,
                $"X would not start the media upload. {await DescribeFailureAsync(initResponse, cancellationToken)}",
                (int)initResponse.StatusCode);
        }

        var initPayload = await ReadJsonAsync(initResponse, cancellationToken);
        var mediaId = initPayload.TryGetProperty("data", out var initData)
            ? Str(initData, "id") ?? Str(initData, "media_id_string")
            : Str(initPayload, "media_id_string") ?? Str(initPayload, "id");

        if (mediaId is null)
        {
            return new MediaUpload(null, "X started the upload but returned no media id.", null);
        }

        await using var stream = await image.OpenRead(cancellationToken);
        var buffer = new byte[ChunkBytes];
        var segment = 0;

        while (true)
        {
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
            if (read == 0)
            {
                break;
            }

            using var chunk = new MultipartFormDataContent();
            var part = new ByteArrayContent(buffer, 0, read);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            chunk.Add(part, "media", "chunk");
            chunk.Add(new StringContent(segment.ToString()), "segment_index");

            using var appendRequest = new HttpRequestMessage(
                HttpMethod.Post, $"{ApiBase}/2/media/upload/{mediaId}/append") { Content = chunk };
            appendRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var appendResponse = await client.SendAsync(appendRequest, cancellationToken);
            if (!appendResponse.IsSuccessStatusCode)
            {
                return new MediaUpload(null,
                    $"X rejected segment {segment} of the image. {await DescribeFailureAsync(appendResponse, cancellationToken)}",
                    (int)appendResponse.StatusCode);
            }

            segment++;

            if (read < buffer.Length)
            {
                break;
            }
        }

        using var finalizeRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{ApiBase}/2/media/upload/{mediaId}/finalize");
        finalizeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var finalizeResponse = await client.SendAsync(finalizeRequest, cancellationToken);
        if (!finalizeResponse.IsSuccessStatusCode)
        {
            return new MediaUpload(null,
                $"X would not finalize the image upload. {await DescribeFailureAsync(finalizeResponse, cancellationToken)}",
                (int)finalizeResponse.StatusCode);
        }

        return new MediaUpload(mediaId, null, null);
    }

    private sealed record TokenResponse(string? AccessToken, string? RefreshToken, DateTimeOffset? ExpiresUtc, string? Error)
    {
        public Dictionary<string, string> ToDictionary(string? username, string? fallbackRefreshToken = null)
        {
            var result = new Dictionary<string, string> { ["accessToken"] = AccessToken ?? string.Empty };

            var refresh = RefreshToken ?? fallbackRefreshToken;
            if (refresh is not null)
            {
                result["refreshToken"] = refresh;
            }

            if (username is not null)
            {
                result["username"] = username;
            }

            return result;
        }
    }

    private async Task<TokenResponse> ExchangeAsync(
        HttpClient client,
        IReadOnlyDictionary<string, string> credentials,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/2/oauth2/token")
        {
            Content = new FormUrlEncodedContent(form)
        };

        // Confidential clients authenticate the token call with basic auth. Public clients do not.
        var clientSecret = Value(credentials, ClientSecretKey);
        if (clientSecret is not null)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Require(credentials, ClientIdKey, "Client id")}:{clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new TokenResponse(null, null, null,
                $"X would not issue a token. {await DescribeFailureAsync(response, cancellationToken)}");
        }

        var payload = await ReadJsonAsync(response, cancellationToken);
        var accessToken = Str(payload, "access_token");
        if (accessToken is null)
        {
            return new TokenResponse(null, null, null, "X returned a token response with no access token.");
        }

        DateTimeOffset? expires = payload.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.UtcNow.AddSeconds(e.GetInt32())
            : null;

        return new TokenResponse(accessToken, Str(payload, "refresh_token"), expires, null);
    }

    private sealed record Me(string? Id, string? Username, string? Error);

    private async Task<Me> ReadMeAsync(HttpClient client, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/2/users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new Me(null, null, await DescribeFailureAsync(response, cancellationToken));
        }

        var payload = await ReadJsonAsync(response, cancellationToken);
        return payload.TryGetProperty("data", out var data)
            ? new Me(Str(data, "id"), Str(data, "username"), null)
            : new Me(null, null, "X returned an account response with no data block.");
    }
}
