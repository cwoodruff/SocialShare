using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Platforms.Shared;

namespace SocialShare.Platforms.Mastodon;

/// <summary>
/// Mastodon. There is no central developer portal: the app registers itself with whichever
/// instance the user names, then runs an ordinary OAuth 2.0 authorization code flow against it.
/// The character limit is per instance, so it is read from the instance API and cached.
///
/// Docs: https://docs.joinmastodon.org/methods/apps/
/// Docs: https://docs.joinmastodon.org/methods/oauth/
/// Docs: https://docs.joinmastodon.org/methods/statuses/
/// Docs: https://docs.joinmastodon.org/methods/media/
/// Docs: https://docs.joinmastodon.org/methods/instance/
/// </summary>
public sealed class MastodonPlatform(IHttpClientFactory httpClientFactory, ILogger<MastodonPlatform> logger)
    : PlatformBase(httpClientFactory, logger)
{
    public const string InstanceUrlKey = "instanceUrl";
    public const string ClientIdKey = "clientId";
    public const string ClientSecretKey = "clientSecret";

    private const string Scopes = "read:accounts write:statuses write:media";

    public override SocialPlatform Platform => SocialPlatform.Mastodon;

    public override PlatformCapabilities Capabilities { get; } = new()
    {
        Platform = SocialPlatform.Mastodon,
        DefaultCharacterLimit = 500,
        DisplayName = "Mastodon",
        AuthKind = PlatformAuthKind.OAuth2,
        RecommendedImage = "Most instances cap uploads at 8 MB and resize above 1920 by 1080. Landscape 1200 by 675 is safe.",
        RecommendedAspectRatio = 1.78,
        SetupDocAnchor = "mastodon",
        Summary = "Point it at your instance and authorize. SocialShare registers itself with that instance for you.",
        CredentialFields =
        [
            new CredentialField(InstanceUrlKey, "Instance URL",
                "The server your account lives on, for example https://mastodon.social or https://hachyderm.io.",
                CredentialFieldKind.Url, true, "https://mastodon.social")
        ]
    };

    private static string Instance(IReadOnlyDictionary<string, string> credentials) =>
        Require(credentials, InstanceUrlKey, "Instance URL").TrimEnd('/');

    public override async Task<PlatformAuthorizationStart> StartAuthorizationAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        OAuthStartContext context,
        CancellationToken cancellationToken)
    {
        string instance;
        try
        {
            instance = Instance(credentials);
        }
        catch (PlatformConfigurationException ex)
        {
            return new PlatformAuthorizationStart(null, null, ex.Message);
        }

        if (!Uri.TryCreate(instance, UriKind.Absolute, out _))
        {
            return new PlatformAuthorizationStart(null, null, "That instance URL does not look like a URL.");
        }

        var updated = new Dictionary<string, string>(credentials);

        // Register once per instance, then reuse the stored client credentials.
        if (Value(credentials, ClientIdKey) is null || Value(credentials, ClientSecretKey) is null)
        {
            using var client = NewClient();
            using var response = await client.PostAsJsonAsync($"{instance}/api/v1/apps", new
            {
                client_name = "SocialShare",
                redirect_uris = context.RedirectUri,
                scopes = Scopes,
                website = context.RedirectUri
            }, Json, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new PlatformAuthorizationStart(null, null,
                    $"That instance would not register SocialShare. {await DescribeFailureAsync(response, cancellationToken)}");
            }

            var app = await ReadJsonAsync(response, cancellationToken);
            var clientId = Str(app, "client_id");
            var clientSecret = Str(app, "client_secret");

            if (clientId is null || clientSecret is null)
            {
                return new PlatformAuthorizationStart(null, null,
                    "That instance registered SocialShare but did not return client credentials.");
            }

            updated[ClientIdKey] = clientId;
            updated[ClientSecretKey] = clientSecret;
        }

        var query = OAuthHelpers.BuildQuery(
        [
            new("client_id", updated[ClientIdKey]),
            new("redirect_uri", context.RedirectUri),
            new("response_type", "code"),
            new("scope", Scopes),
            new("state", context.State)
        ]);

        return new PlatformAuthorizationStart(new Uri($"{instance}/oauth/authorize?{query}"), updated);
    }

    public override async Task<PlatformConnectResult> CompleteAuthorizationAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        OAuthCallbackContext context,
        CancellationToken cancellationToken)
    {
        var instance = Instance(credentials);

        using var client = NewClient();
        using var response = await client.PostAsync(
            $"{instance}/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = Require(credentials, ClientIdKey, "Client id"),
                ["client_secret"] = Require(credentials, ClientSecretKey, "Client secret"),
                ["redirect_uri"] = context.RedirectUri,
                ["code"] = context.Code,
                ["scope"] = Scopes
            }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new PlatformConnectResult(false,
                Error: $"The instance would not exchange the code. {await DescribeFailureAsync(response, cancellationToken)}");
        }

        var payload = await ReadJsonAsync(response, cancellationToken);
        var accessToken = Str(payload, "access_token");
        if (accessToken is null)
        {
            return new PlatformConnectResult(false, Error: "The instance returned no access token.");
        }

        var tokens = new Dictionary<string, string> { ["accessToken"] = accessToken };
        var verified = await VerifyAsync(client, instance, accessToken, cancellationToken);
        var limit = await ReadCharacterLimitAsync(client, instance, cancellationToken);

        return new PlatformConnectResult(
            true,
            DisplayName: verified.Handle is null ? null : "@" + verified.Handle,
            RemoteAccountId: verified.Id,
            Tokens: tokens,
            // Mastodon access tokens do not expire on their own.
            TokenExpiresUtc: null,
            CharacterLimit: limit);
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
            return new PlatformTestResult(false, "Mastodon is not connected yet.");
        }

        using var client = NewClient();
        var verified = await VerifyAsync(client, Instance(credentials), accessToken, cancellationToken);

        return verified.Handle is null
            ? new PlatformTestResult(false, verified.Error ?? "The instance rejected the stored token.")
            : new PlatformTestResult(true, $"Connected as @{verified.Handle}.", "@" + verified.Handle);
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
            return new PlatformPublishResult(false, Error: "Mastodon is not connected. Connect it and retry.");
        }

        var instance = Instance(credentials);
        using var client = NewClient();

        string? mediaId = null;
        if (request.Image is not null)
        {
            var upload = await UploadMediaAsync(client, instance, accessToken, request.Image, cancellationToken);
            if (upload.Error is not null)
            {
                return new PlatformPublishResult(false, Error: upload.Error, HttpStatusCode: upload.StatusCode);
            }

            mediaId = upload.MediaId;
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("status", request.Body),
            new("visibility", "public")
        };

        if (mediaId is not null)
        {
            form.Add(new KeyValuePair<string, string>("media_ids[]", mediaId));
        }

        using var statusRequest = new HttpRequestMessage(HttpMethod.Post, $"{instance}/api/v1/statuses")
        {
            Content = new FormUrlEncodedContent(form)
        };
        statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Stops a retry from posting twice if the response is lost on the way back.
        statusRequest.Headers.TryAddWithoutValidation("Idempotency-Key", IdempotencyKey(request));

        using var statusResponse = await client.SendAsync(statusRequest, cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
        {
            return new PlatformPublishResult(false,
                Error: await DescribeFailureAsync(statusResponse, cancellationToken),
                HttpStatusCode: (int)statusResponse.StatusCode);
        }

        var status = await ReadJsonAsync(statusResponse, cancellationToken);
        return new PlatformPublishResult(true, Str(status, "id"), Str(status, "url"));
    }

    private static string IdempotencyKey(PlatformPublishRequest request) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{request.Account.Id}|{request.Body}")))[..32];

    private sealed record MediaUpload(string? MediaId, string? Error, int? StatusCode);

    private async Task<MediaUpload> UploadMediaAsync(
        HttpClient client,
        string instance,
        string accessToken,
        PublishImage image,
        CancellationToken cancellationToken)
    {
        await using var stream = await image.OpenRead(cancellationToken);
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        content.Add(file, "file", "upload" + ExtensionFor(image.ContentType));

        if (!string.IsNullOrWhiteSpace(image.AltText))
        {
            content.Add(new StringContent(image.AltText), "description");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{instance}/api/v2/media") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new MediaUpload(null,
                $"The instance rejected the image. {await DescribeFailureAsync(response, cancellationToken)}",
                (int)response.StatusCode);
        }

        var media = await ReadJsonAsync(response, cancellationToken);
        var id = Str(media, "id");
        if (id is null)
        {
            return new MediaUpload(null, "The instance accepted the image but returned no media id.", null);
        }

        // A 202 means the instance is still processing. Poll until it is ready to attach.
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            await WaitForMediaAsync(client, instance, accessToken, id, cancellationToken);
        }

        return new MediaUpload(id, null, null);
    }

    private static async Task WaitForMediaAsync(
        HttpClient client,
        string instance,
        string accessToken,
        string mediaId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{instance}/api/v1/media/{mediaId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                return;
            }
        }
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg"
    };

    private sealed record VerifiedAccount(string? Id, string? Handle, string? Error);

    private async Task<VerifiedAccount> VerifyAsync(
        HttpClient client,
        string instance,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{instance}/api/v1/accounts/verify_credentials");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new VerifiedAccount(null, null, await DescribeFailureAsync(response, cancellationToken));
        }

        var payload = await ReadJsonAsync(response, cancellationToken);
        return new VerifiedAccount(Str(payload, "id"), Str(payload, "acct") ?? Str(payload, "username"), null);
    }

    /// <summary>Reads the instance character limit so the compose screen counts against the real number.</summary>
    private async Task<int?> ReadCharacterLimitAsync(HttpClient client, string instance, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync($"{instance}/api/v2/instance", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await ReadJsonAsync(response, cancellationToken);
            if (payload.TryGetProperty("configuration", out var configuration)
                && configuration.TryGetProperty("statuses", out var statuses)
                && statuses.TryGetProperty("max_characters", out var max)
                && max.ValueKind == JsonValueKind.Number)
            {
                return max.GetInt32();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Logger.LogDebug(ex, "Could not read the character limit from {Instance}.", instance);
        }

        return null;
    }
}
