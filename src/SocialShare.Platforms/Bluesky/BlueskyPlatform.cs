using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Core.Text;
using SocialShare.Platforms.Shared;

namespace SocialShare.Platforms.Bluesky;

/// <summary>
/// Bluesky over the AT Protocol. This is the one platform where a per app password is the
/// supported way in, so there is no browser redirect: the handle and app password are exchanged
/// for a session, and the session is refreshed from the refresh token after that.
///
/// Docs: https://docs.bsky.app/docs/advanced-guides/atproto
/// Docs: https://docs.bsky.app/docs/api/com-atproto-server-create-session
/// Docs: https://docs.bsky.app/docs/api/com-atproto-repo-create-record
/// Docs: https://docs.bsky.app/docs/advanced-guides/post-richtext
/// </summary>
public sealed class BlueskyPlatform(IHttpClientFactory httpClientFactory, ILogger<BlueskyPlatform> logger)
    : PlatformBase(httpClientFactory, logger)
{
    public const string HandleKey = "handle";
    public const string AppPasswordKey = "appPassword";
    public const string PdsHostKey = "pdsHost";

    private const string DefaultPds = "https://bsky.social";

    public override SocialPlatform Platform => SocialPlatform.Bluesky;

    public override PlatformCapabilities Capabilities { get; } = new()
    {
        Platform = SocialPlatform.Bluesky,
        DisplayName = "Bluesky",
        AuthKind = PlatformAuthKind.DirectCredentials,
        DefaultCharacterLimit = 300,
        ImageSupported = true,
        RecommendedImage = "Up to 2000 by 2000, under 1 MB after upload. Landscape 1200 by 675 looks best in the feed.",
        RecommendedAspectRatio = 1.78,
        SetupDocAnchor = "bluesky",
        Summary = "Uses an app password you create in Bluesky settings. Nothing else to register.",
        CredentialFields =
        [
            new CredentialField(HandleKey, "Handle", "Your full handle, for example woodruff.dev or name.bsky.social.",
                CredentialFieldKind.Text, true, "name.bsky.social"),
            new CredentialField(AppPasswordKey, "App password",
                "Create one in Bluesky under Settings, Privacy and security, App passwords. This is not your account password.",
                CredentialFieldKind.Secret, true, "xxxx-xxxx-xxxx-xxxx"),
            new CredentialField(PdsHostKey, "PDS host",
                "Leave this alone unless you self host your data server.",
                CredentialFieldKind.Url, false, DefaultPds, DefaultPds)
        ]
    };

    private static string Pds(IReadOnlyDictionary<string, string> credentials) =>
        (Value(credentials, PdsHostKey) ?? DefaultPds).TrimEnd('/');

    public override async Task<PlatformConnectResult> ConnectDirectAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken)
    {
        var handle = Require(credentials, HandleKey, "Handle").TrimStart('@');
        var appPassword = Require(credentials, AppPasswordKey, "App password");

        using var client = NewClient();
        using var response = await client.PostAsJsonAsync(
            $"{Pds(credentials)}/xrpc/com.atproto.server.createSession",
            new { identifier = handle, password = appPassword },
            Json,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new PlatformConnectResult(false,
                Error: $"Bluesky refused the handle and app password. {await DescribeFailureAsync(response, cancellationToken)}");
        }

        var session = await ReadJsonAsync(response, cancellationToken);
        var did = Str(session, "did");
        if (did is null)
        {
            return new PlatformConnectResult(false, Error: "Bluesky returned a session without a DID.");
        }

        return new PlatformConnectResult(
            true,
            DisplayName: "@" + (Str(session, "handle") ?? handle),
            RemoteAccountId: did,
            Tokens: new Dictionary<string, string>
            {
                ["accessJwt"] = Str(session, "accessJwt") ?? string.Empty,
                ["refreshJwt"] = Str(session, "refreshJwt") ?? string.Empty,
                ["did"] = did
            },
            // Access tokens are short lived, so the refresh path below does the real work.
            TokenExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(100));
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

        var refreshJwt = Value(tokens, "refreshJwt");
        if (refreshJwt is null)
        {
            // No refresh token to work with, so fall back to a fresh session from the app password.
            return await ConnectDirectAsync(account, credentials, cancellationToken);
        }

        using var client = NewClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{Pds(credentials)}/xrpc/com.atproto.server.refreshSession");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshJwt);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Refresh tokens expire too. A new session from the stored app password is the fix.
            return await ConnectDirectAsync(account, credentials, cancellationToken);
        }

        var session = await ReadJsonAsync(response, cancellationToken);
        var did = Str(session, "did") ?? Value(tokens, "did");
        if (did is null)
        {
            return await ConnectDirectAsync(account, credentials, cancellationToken);
        }

        return new PlatformConnectResult(
            true,
            DisplayName: "@" + (Str(session, "handle") ?? account.DisplayName?.TrimStart('@') ?? string.Empty),
            RemoteAccountId: did,
            Tokens: new Dictionary<string, string>
            {
                ["accessJwt"] = Str(session, "accessJwt") ?? string.Empty,
                ["refreshJwt"] = Str(session, "refreshJwt") ?? refreshJwt,
                ["did"] = did
            },
            TokenExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(100));
    }

    public override async Task<PlatformTestResult> TestAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        var accessJwt = Value(tokens, "accessJwt");
        var did = Value(tokens, "did") ?? account.RemoteAccountId;

        if (accessJwt is null || did is null)
        {
            return new PlatformTestResult(false, "Bluesky is not connected yet.");
        }

        using var client = NewClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{Pds(credentials)}/xrpc/app.bsky.actor.getProfile?actor={Uri.EscapeDataString(did)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessJwt);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new PlatformTestResult(false, await DescribeFailureAsync(response, cancellationToken));
        }

        var profile = await ReadJsonAsync(response, cancellationToken);
        var handle = Str(profile, "handle");
        return new PlatformTestResult(true, $"Connected as @{handle}.", handle is null ? null : "@" + handle);
    }

    public override async Task<PlatformPublishResult> PublishAsync(
        PlatformPublishRequest request,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        var accessJwt = Value(tokens, "accessJwt");
        var did = Value(tokens, "did") ?? request.Account.RemoteAccountId;

        if (accessJwt is null || did is null)
        {
            return new PlatformPublishResult(false, Error: "Bluesky is not connected. Connect it and retry.");
        }

        var pds = Pds(credentials);
        using var client = NewClient();

        object? embed = null;
        if (request.Image is not null)
        {
            var blob = await UploadBlobAsync(client, pds, accessJwt, request.Image, cancellationToken);
            if (blob.Error is not null)
            {
                return new PlatformPublishResult(false, Error: blob.Error, HttpStatusCode: blob.StatusCode);
            }

            embed = new
            {
                type = "app.bsky.embed.images",
                images = new[]
                {
                    new
                    {
                        alt = request.Image.AltText ?? string.Empty,
                        image = blob.Blob,
                        aspectRatio = new { width = request.Image.Width, height = request.Image.Height }
                    }
                }
            };
        }

        var facets = await BuildFacetsAsync(client, pds, accessJwt, request.Body, cancellationToken);

        var record = new Dictionary<string, object?>
        {
            ["$type"] = "app.bsky.feed.post",
            ["text"] = request.Body,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        };

        if (facets.Count > 0)
        {
            record["facets"] = facets;
        }

        if (embed is not null)
        {
            // The embed union needs its discriminator under the $type key, which an anonymous
            // type cannot express, so it is rebuilt as a dictionary here.
            record["embed"] = ToEmbedDictionary(embed);
        }

        using var createRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{pds}/xrpc/com.atproto.repo.createRecord")
        {
            Content = JsonContent.Create(new
            {
                repo = did,
                collection = "app.bsky.feed.post",
                record
            }, options: Json)
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessJwt);

        using var createResponse = await client.SendAsync(createRequest, cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
        {
            return new PlatformPublishResult(false,
                Error: await DescribeFailureAsync(createResponse, cancellationToken),
                HttpStatusCode: (int)createResponse.StatusCode);
        }

        var created = await ReadJsonAsync(createResponse, cancellationToken);
        var uri = Str(created, "uri");
        var handle = request.Account.DisplayName?.TrimStart('@') ?? did;

        return new PlatformPublishResult(
            true,
            RemotePostId: uri,
            RemoteUrl: uri is null ? null : $"https://bsky.app/profile/{handle}/post/{uri[(uri.LastIndexOf('/') + 1)..]}");
    }

    private static Dictionary<string, object?> ToEmbedDictionary(object embed)
    {
        // Round trip through JSON so the anonymous "type" member lands as "$type".
        var json = JsonSerializer.SerializeToElement(embed, Json);
        var result = new Dictionary<string, object?>();
        foreach (var property in json.EnumerateObject())
        {
            result[property.Name == "type" ? "$type" : property.Name] = property.Value;
        }

        return result;
    }

    private sealed record BlobUpload(JsonElement? Blob, string? Error, int? StatusCode);

    private async Task<BlobUpload> UploadBlobAsync(
        HttpClient client,
        string pds,
        string accessJwt,
        PublishImage image,
        CancellationToken cancellationToken)
    {
        await using var stream = await image.OpenRead(cancellationToken);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{pds}/xrpc/com.atproto.repo.uploadBlob")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessJwt);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new BlobUpload(null,
                $"Bluesky rejected the image. {await DescribeFailureAsync(response, cancellationToken)}",
                (int)response.StatusCode);
        }

        var payload = await ReadJsonAsync(response, cancellationToken);
        if (!payload.TryGetProperty("blob", out var blob))
        {
            return new BlobUpload(null, "Bluesky accepted the image but did not return a blob reference.", null);
        }

        return new BlobUpload(blob.Clone(), null, null);
    }

    /// <summary>
    /// Builds the facet list for links and mentions. Offsets are UTF-8 byte positions, which is
    /// why the scanning happens in RichText rather than over .NET string indexes.
    /// </summary>
    private async Task<List<object>> BuildFacetsAsync(
        HttpClient client,
        string pds,
        string accessJwt,
        string body,
        CancellationToken cancellationToken)
    {
        var facets = new List<object>();

        foreach (var span in RichText.FindSpans(body))
        {
            if (span.Kind == TextSpanKind.Link)
            {
                facets.Add(new
                {
                    index = new { byteStart = span.ByteStart, byteEnd = span.ByteEnd },
                    features = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["$type"] = "app.bsky.richtext.facet#link",
                            ["uri"] = span.Text
                        }
                    }
                });
                continue;
            }

            var did = await ResolveHandleAsync(client, pds, accessJwt, span.Text, cancellationToken);
            if (did is null)
            {
                // An unresolvable mention is not an error, it just stays as plain text.
                continue;
            }

            facets.Add(new
            {
                index = new { byteStart = span.ByteStart, byteEnd = span.ByteEnd },
                features = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["$type"] = "app.bsky.richtext.facet#mention",
                        ["did"] = did
                    }
                }
            });
        }

        return facets;
    }

    private async Task<string?> ResolveHandleAsync(
        HttpClient client,
        string pds,
        string accessJwt,
        string handle,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{pds}/xrpc/com.atproto.identity.resolveHandle?handle={Uri.EscapeDataString(handle)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessJwt);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return Str(await ReadJsonAsync(response, cancellationToken), "did");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.LogDebug(ex, "Could not resolve the Bluesky handle {Handle}.", handle);
            return null;
        }
    }
}
