using System.Net;
using System.Text.Json;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Platforms.Bluesky;
using SocialShare.Tests.Support;
using static SocialShare.Tests.Support.TestHelpers;

namespace SocialShare.Tests;

public class BlueskyPlatformTests
{
    private static readonly Dictionary<string, string> Credentials = Map(
        (BlueskyPlatform.HandleKey, "woodruff.dev"),
        (BlueskyPlatform.AppPasswordKey, "abcd-efgh-ijkl-mnop"),
        (BlueskyPlatform.PdsHostKey, "https://bsky.social"));

    private static BlueskyPlatform Platform(StubHttpMessageHandler stub) =>
        new(stub.AsFactory(), Logger<BlueskyPlatform>());

    [Fact]
    public async Task Connecting_exchanges_the_app_password_for_a_session()
    {
        var stub = new StubHttpMessageHandler().RespondJson("createSession", """
            {"did":"did:plc:abc","handle":"woodruff.dev","accessJwt":"access","refreshJwt":"refresh"}
            """);

        var result = await Platform(stub).ConnectDirectAsync(
            Account(SocialPlatform.Bluesky), Credentials, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("did:plc:abc", result.RemoteAccountId);
        Assert.Equal("@woodruff.dev", result.DisplayName);
        Assert.Equal("access", result.Tokens!["accessJwt"]);
        Assert.Equal("refresh", result.Tokens["refreshJwt"]);

        var sent = JsonDocument.Parse(stub.Requests.Single().Body).RootElement;
        Assert.Equal("woodruff.dev", sent.GetProperty("identifier").GetString());
        Assert.Equal("abcd-efgh-ijkl-mnop", sent.GetProperty("password").GetString());
    }

    [Fact]
    public async Task A_rejected_app_password_comes_back_as_a_readable_error()
    {
        var stub = new StubHttpMessageHandler().RespondJson(
            "createSession",
            """{"error":"AuthenticationRequired","message":"Invalid identifier or password"}""",
            HttpStatusCode.Unauthorized);

        var result = await Platform(stub).ConnectDirectAsync(
            Account(SocialPlatform.Bluesky), Credentials, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("401", result.Error);
        Assert.Contains("Invalid identifier or password", result.Error);
    }

    [Fact]
    public async Task Publishing_text_computes_link_and_mention_facets()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("resolveHandle", """{"did":"did:plc:friend"}""")
            .RespondJson("createRecord", """{"uri":"at://did:plc:abc/app.bsky.feed.post/xyz789","cid":"bafy"}""");

        var request = new PlatformPublishRequest(
            Account(SocialPlatform.Bluesky, "did:plc:abc", "@woodruff.dev"),
            "Read this https://woodruff.dev and say hi to @friend.bsky.social",
            null);

        var result = await Platform(stub).PublishAsync(
            request, Credentials, Map(("accessJwt", "access"), ("did", "did:plc:abc")), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://bsky.app/profile/woodruff.dev/post/xyz789", result.RemoteUrl);

        var record = JsonDocument.Parse(stub.Requests.Last().Body).RootElement.GetProperty("record");
        var facets = record.GetProperty("facets").EnumerateArray().ToList();

        Assert.Equal(2, facets.Count);
        Assert.Equal(
            "app.bsky.richtext.facet#link",
            facets[0].GetProperty("features")[0].GetProperty("$type").GetString());
        Assert.Equal(
            "did:plc:friend",
            facets[1].GetProperty("features")[0].GetProperty("did").GetString());

        // Offsets are UTF-8 byte positions, which is what the AT Protocol wants.
        Assert.Equal(10, facets[0].GetProperty("index").GetProperty("byteStart").GetInt32());
        Assert.Equal(30, facets[0].GetProperty("index").GetProperty("byteEnd").GetInt32());
    }

    [Fact]
    public async Task Publishing_an_image_uploads_a_blob_first_and_embeds_it()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("uploadBlob", """{"blob":{"$type":"blob","ref":{"$link":"bafkreiabc"},"mimeType":"image/png","size":37}}""")
            .RespondJson("createRecord", """{"uri":"at://did:plc:abc/app.bsky.feed.post/withimage","cid":"bafy"}""");

        var request = new PlatformPublishRequest(
            Account(SocialPlatform.Bluesky, "did:plc:abc", "@woodruff.dev"),
            "With a picture",
            Image());

        var result = await Platform(stub).PublishAsync(
            request, Credentials, Map(("accessJwt", "access"), ("did", "did:plc:abc")), CancellationToken.None);

        Assert.True(result.Success);

        var upload = stub.Requests.First(r => r.UriEndsWith("com.atproto.repo.uploadBlob"));
        Assert.Equal("image/png", upload.Headers.ContentType);
        Assert.Equal("Bearer access", upload.Headers.Authorization);

        var embed = JsonDocument.Parse(stub.Requests.Last().Body).RootElement
            .GetProperty("record").GetProperty("embed");

        Assert.Equal("app.bsky.embed.images", embed.GetProperty("$type").GetString());

        var image = embed.GetProperty("images")[0];
        Assert.Equal("Alt text", image.GetProperty("alt").GetString());
        Assert.Equal("bafkreiabc", image.GetProperty("image").GetProperty("ref").GetProperty("$link").GetString());
        Assert.Equal(1200, image.GetProperty("aspectRatio").GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task An_expired_session_is_refreshed_from_the_refresh_token()
    {
        var stub = new StubHttpMessageHandler().RespondJson("refreshSession", """
            {"did":"did:plc:abc","handle":"woodruff.dev","accessJwt":"fresh","refreshJwt":"newrefresh"}
            """);

        var account = Account(SocialPlatform.Bluesky, "did:plc:abc");
        account.TokenExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);

        var result = await Platform(stub).RefreshAsync(
            account, Credentials, Map(("accessJwt", "stale"), ("refreshJwt", "refresh")), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal("fresh", result.Tokens!["accessJwt"]);
        Assert.Equal("Bearer refresh", stub.Requests.Single().Headers.Authorization);
    }

    [Fact]
    public async Task A_session_that_is_still_valid_is_left_alone()
    {
        var stub = new StubHttpMessageHandler();
        var account = Account(SocialPlatform.Bluesky, "did:plc:abc");
        account.TokenExpiresUtc = DateTimeOffset.UtcNow.AddHours(1);

        var result = await Platform(stub).RefreshAsync(
            account, Credentials, Map(("accessJwt", "good"), ("refreshJwt", "refresh")), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task A_dead_refresh_token_falls_back_to_a_fresh_session()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("refreshSession", """{"error":"ExpiredToken"}""", HttpStatusCode.BadRequest)
            .RespondJson("createSession", """
                {"did":"did:plc:abc","handle":"woodruff.dev","accessJwt":"rebuilt","refreshJwt":"r2"}
                """);

        var account = Account(SocialPlatform.Bluesky, "did:plc:abc");
        account.TokenExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);

        var result = await Platform(stub).RefreshAsync(
            account, Credentials, Map(("refreshJwt", "dead")), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal("rebuilt", result.Tokens!["accessJwt"]);
    }

    [Fact]
    public async Task Testing_the_connection_reads_the_profile()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("getProfile", """{"did":"did:plc:abc","handle":"woodruff.dev"}""");

        var result = await Platform(stub).TestAsync(
            Account(SocialPlatform.Bluesky, "did:plc:abc"),
            Credentials,
            Map(("accessJwt", "access"), ("did", "did:plc:abc")),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("@woodruff.dev", result.DisplayName);
        Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, stub.Requests[0].Method);
    }

    [Fact]
    public void The_capabilities_match_what_bluesky_actually_allows()
    {
        var capabilities = Platform(new StubHttpMessageHandler()).Capabilities;

        Assert.Equal(300, capabilities.DefaultCharacterLimit);
        Assert.Equal(PlatformAuthKind.DirectCredentials, capabilities.AuthKind);
        Assert.False(capabilities.ImageRequired);
        Assert.False(capabilities.RequiresPublicImageUrl);
    }
}
