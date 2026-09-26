using System.Net;
using System.Text.Json;
using System.Web;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Platforms.Instagram;
using SocialShare.Platforms.LinkedIn;
using SocialShare.Platforms.Mastodon;
using SocialShare.Platforms.Threads;
using SocialShare.Platforms.X;
using SocialShare.Tests.Support;
using static SocialShare.Tests.Support.TestHelpers;

namespace SocialShare.Tests;

public class LinkedInPlatformTests
{
    private static readonly Dictionary<string, string> Credentials =
        Map((LinkedInPlatform.ClientIdKey, "client-id"), (LinkedInPlatform.ClientSecretKey, "client-secret"));

    private static LinkedInPlatform Platform(StubHttpMessageHandler stub) =>
        new(stub.AsFactory(), Logger<LinkedInPlatform>());

    [Fact]
    public async Task The_authorization_url_asks_for_the_member_posting_scope()
    {
        var start = await Platform(new StubHttpMessageHandler()).StartAuthorizationAsync(
            Account(SocialPlatform.LinkedIn),
            Credentials,
            new OAuthStartContext("https://app.test/callback/LinkedIn", "state123", null),
            CancellationToken.None);

        Assert.NotNull(start.AuthorizationUrl);
        var query = HttpUtility.ParseQueryString(start.AuthorizationUrl.Query);

        Assert.Equal("client-id", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("state123", query["state"]);
        Assert.Contains("w_member_social", query["scope"]);
        Assert.Equal("https://app.test/callback/LinkedIn", query["redirect_uri"]);
    }

    [Fact]
    public async Task Completing_the_flow_stores_the_member_urn()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("accessToken", """{"access_token":"token","expires_in":5184000,"refresh_token":"refresh"}""")
            .RespondJson("userinfo", """{"sub":"AbC123","name":"Chris Woodruff"}""");

        var result = await Platform(stub).CompleteAuthorizationAsync(
            Account(SocialPlatform.LinkedIn),
            Credentials,
            new OAuthCallbackContext("https://app.test/callback/LinkedIn", "code", null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("urn:li:person:AbC123", result.RemoteAccountId);
        Assert.Equal("Chris Woodruff", result.DisplayName);
        Assert.Equal("refresh", result.Tokens!["refreshToken"]);
        Assert.True(result.TokenExpiresUtc > DateTimeOffset.UtcNow.AddDays(59));
    }

    [Fact]
    public async Task Publishing_with_an_image_initializes_an_upload_then_references_the_urn()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("images?action=initializeUpload", """
                {"value":{"uploadUrl":"https://upload.linkedin.test/put","image":"urn:li:image:C4E123"}}
                """)
            .RespondText("upload.linkedin.test", "", HttpStatusCode.Created)
            .RespondText("rest/posts", "", HttpStatusCode.Created, ("x-restli-id", "urn:li:share:987"));

        var result = await Platform(stub).PublishAsync(
            new PlatformPublishRequest(Account(SocialPlatform.LinkedIn, "urn:li:person:AbC123"), "Hello LinkedIn", Image()),
            Credentials,
            Map(("accessToken", "token"), ("memberUrn", "urn:li:person:AbC123")),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("urn:li:share:987", result.RemotePostId);
        Assert.Equal("https://www.linkedin.com/feed/update/urn:li:share:987/", result.RemoteUrl);

        var put = stub.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("https://upload.linkedin.test/put", put.Uri.ToString());

        var post = stub.Requests.Last();
        var body = JsonDocument.Parse(post.Body).RootElement;
        Assert.Equal("urn:li:person:AbC123", body.GetProperty("author").GetString());
        Assert.Equal("Hello LinkedIn", body.GetProperty("commentary").GetString());
        Assert.Equal("urn:li:image:C4E123", body.GetProperty("content").GetProperty("media").GetProperty("id").GetString());
        Assert.Equal("PUBLISHED", body.GetProperty("lifecycleState").GetString());

        // The versioned APIs refuse the call without both of these headers.
        Assert.True(post.Headers.All.ContainsKey("LinkedIn-Version"));
        Assert.Equal("2.0.0", post.Headers.All["X-Restli-Protocol-Version"]);
    }

    [Fact]
    public async Task A_rejected_post_reports_the_platform_status_and_body()
    {
        var stub = new StubHttpMessageHandler()
            .RespondText("rest/posts", """{"message":"ACCESS_DENIED","status":403}""", HttpStatusCode.Forbidden);

        var result = await Platform(stub).PublishAsync(
            new PlatformPublishRequest(Account(SocialPlatform.LinkedIn, "urn:li:person:x"), "Nope", null),
            Credentials,
            Map(("accessToken", "token"), ("memberUrn", "urn:li:person:x")),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(403, result.HttpStatusCode);
        Assert.Contains("ACCESS_DENIED", result.Error);
    }
}

public class MastodonPlatformTests
{
    private static MastodonPlatform Platform(StubHttpMessageHandler stub) =>
        new(stub.AsFactory(), Logger<MastodonPlatform>());

    [Fact]
    public async Task Starting_a_connection_registers_the_app_with_the_instance()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("/api/v1/apps", """{"client_id":"generated-id","client_secret":"generated-secret"}""");

        var start = await Platform(stub).StartAuthorizationAsync(
            Account(SocialPlatform.Mastodon),
            Map((MastodonPlatform.InstanceUrlKey, "https://hachyderm.io")),
            new OAuthStartContext("https://app.test/callback/Mastodon", "state", null),
            CancellationToken.None);

        Assert.NotNull(start.AuthorizationUrl);
        Assert.StartsWith("https://hachyderm.io/oauth/authorize", start.AuthorizationUrl.ToString());

        // The generated credentials have to be handed back so the caller can store them.
        Assert.Equal("generated-id", start.UpdatedCredentials![MastodonPlatform.ClientIdKey]);
        Assert.Equal("generated-secret", start.UpdatedCredentials[MastodonPlatform.ClientSecretKey]);
    }

    [Fact]
    public async Task An_instance_that_is_already_registered_is_not_registered_again()
    {
        var stub = new StubHttpMessageHandler();

        var start = await Platform(stub).StartAuthorizationAsync(
            Account(SocialPlatform.Mastodon),
            Map((MastodonPlatform.InstanceUrlKey, "https://hachyderm.io"),
                (MastodonPlatform.ClientIdKey, "known"),
                (MastodonPlatform.ClientSecretKey, "secret")),
            new OAuthStartContext("https://app.test/callback/Mastodon", "state", null),
            CancellationToken.None);

        Assert.NotNull(start.AuthorizationUrl);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Connecting_reads_the_per_instance_character_limit()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("/oauth/token", """{"access_token":"token","token_type":"Bearer"}""")
            .RespondJson("verify_credentials", """{"id":"42","acct":"woody"}""")
            .RespondJson("/api/v2/instance", """{"configuration":{"statuses":{"max_characters":1337}}}""");

        var result = await Platform(stub).CompleteAuthorizationAsync(
            Account(SocialPlatform.Mastodon),
            Map((MastodonPlatform.InstanceUrlKey, "https://hachyderm.io"),
                (MastodonPlatform.ClientIdKey, "id"),
                (MastodonPlatform.ClientSecretKey, "secret")),
            new OAuthCallbackContext("https://app.test/callback/Mastodon", "code", null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1337, result.CharacterLimit);
        Assert.Equal("@woody", result.DisplayName);
        Assert.Equal("42", result.RemoteAccountId);
    }

    [Fact]
    public async Task Publishing_uploads_media_then_attaches_it_to_the_status()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("/api/v2/media", """{"id":"media-1","url":"https://cdn.test/1.png"}""")
            .RespondJson("/api/v1/statuses", """{"id":"status-1","url":"https://hachyderm.io/@woody/status-1"}""");

        var result = await Platform(stub).PublishAsync(
            new PlatformPublishRequest(Account(SocialPlatform.Mastodon, "42", "@woody"), "Hello fediverse", Image()),
            Map((MastodonPlatform.InstanceUrlKey, "https://hachyderm.io")),
            Map(("accessToken", "token")),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://hachyderm.io/@woody/status-1", result.RemoteUrl);

        var status = stub.Requests.Last();
        Assert.Contains("media_ids%5B%5D=media-1", status.Body);
        Assert.Contains("status=Hello+fediverse", status.Body);

        // An idempotency key means a retried request cannot post the same status twice.
        Assert.True(status.Headers.All.ContainsKey("Idempotency-Key"));
    }
}

public class XPlatformTests
{
    private static readonly Dictionary<string, string> Credentials = Map((XPlatform.ClientIdKey, "client-id"));

    private static XPlatform Platform(StubHttpMessageHandler stub) => new(stub.AsFactory(), Logger<XPlatform>());

    [Fact]
    public async Task The_authorization_url_carries_the_pkce_challenge()
    {
        var start = await Platform(new StubHttpMessageHandler()).StartAuthorizationAsync(
            Account(SocialPlatform.X),
            Credentials,
            new OAuthStartContext("https://app.test/callback/X", "state", "challenge-value"),
            CancellationToken.None);

        var query = HttpUtility.ParseQueryString(start.AuthorizationUrl!.Query);
        Assert.Equal("challenge-value", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Contains("tweet.write", query["scope"]);
        Assert.Contains("offline.access", query["scope"]);
    }

    [Fact]
    public async Task Completing_the_flow_without_a_verifier_fails_rather_than_guessing()
    {
        var result = await Platform(new StubHttpMessageHandler()).CompleteAuthorizationAsync(
            Account(SocialPlatform.X),
            Credentials,
            new OAuthCallbackContext("https://app.test/callback/X", "code", null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("PKCE", result.Error);
    }

    [Fact]
    public async Task Publishing_with_an_image_runs_the_chunked_upload()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("media/upload/initialize", """{"data":{"id":"media-9"}}""")
            .RespondJson("/append", """{"data":{"expires_after_secs":3600}}""")
            .RespondJson("/finalize", """{"data":{"id":"media-9"}}""")
            .RespondJson("2/tweets", """{"data":{"id":"1800000000000000000","text":"Hello X"}}""");

        var result = await Platform(stub).PublishAsync(
            new PlatformPublishRequest(Account(SocialPlatform.X, "9876"), "Hello X", Image("image/jpeg")),
            Credentials,
            Map(("accessToken", "token"), ("username", "woodruff")),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://x.com/woodruff/status/1800000000000000000", result.RemoteUrl);

        Assert.Contains(stub.Requests, r => r.UriEndsWith("/2/media/upload/initialize"));
        Assert.Contains(stub.Requests, r => r.UriEndsWith("/2/media/upload/media-9/append"));
        Assert.Contains(stub.Requests, r => r.UriEndsWith("/2/media/upload/media-9/finalize"));

        var tweet = JsonDocument.Parse(stub.Requests.Last().Body).RootElement;
        Assert.Equal("media-9", tweet.GetProperty("media").GetProperty("media_ids")[0].GetString());
    }

    [Fact]
    public async Task A_free_tier_403_says_so_in_the_error()
    {
        var stub = new StubHttpMessageHandler().RespondText(
            "2/tweets",
            """{"title":"Unsupported Authentication","status":403}""",
            HttpStatusCode.Forbidden);

        var result = await Platform(stub).PublishAsync(
            new PlatformPublishRequest(Account(SocialPlatform.X, "9876"), "Hello X", null),
            Credentials,
            Map(("accessToken", "token")),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(403, result.HttpStatusCode);
        Assert.Contains("free tier", result.Error);
    }

    [Fact]
    public void X_is_flagged_as_needing_a_paid_tier()
    {
        var capabilities = Platform(new StubHttpMessageHandler()).Capabilities;
        Assert.True(capabilities.RequiresPaidTier);
        Assert.Equal(280, capabilities.DefaultCharacterLimit);
        Assert.Equal(PlatformAuthKind.OAuth2Pkce, capabilities.AuthKind);
    }
}

public class ThreadsPlatformTests
{
    private static readonly Dictionary<string, string> Credentials =
        Map((ThreadsPlatform.ClientIdKey, "app-id"), (ThreadsPlatform.ClientSecretKey, "app-secret"));

    private static ThreadsPlatform Platform(StubHttpMessageHandler stub) =>
        new(stub.AsFactory(), Logger<ThreadsPlatform>());

    [Fact]
    public async Task Connecting_swaps_the_short_token_for_a_long_lived_one()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("oauth/access_token", """{"access_token":"short","user_id":"7788"}""")
            .RespondJson("/access_token?", """{"access_token":"long-lived","expires_in":5184000}""")
            .RespondJson("fields=id%2Cusername", """{"id":"7788","username":"woodruff"}""");

        var result = await Platform(stub).CompleteAuthorizationAsync(
            Account(SocialPlatform.Threads),
            Credentials,
            new OAuthCallbackContext("https://app.test/callback/Threads", "code", null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("long-lived", result.Tokens!["accessToken"]);
        Assert.Equal("7788", result.RemoteAccountId);
        Assert.Equal("@woodruff", result.DisplayName);
    }

    [Fact]
    public async Task Publishing_creates_a_container_then_publishes_it()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("/threads_publish", """{"id":"post-5"}""")
            .RespondJson("/7788/threads", """{"id":"container-4"}""")
            .RespondJson("fields=permalink", """{"permalink":"https://www.threads.net/@woodruff/post/abc"}""");

        var result = await Platform(stub).PublishAsync(
            new PlatformPublishRequest(Account(SocialPlatform.Threads, "7788"), "Hello Threads", null),
            Credentials,
            Map(("accessToken", "token"), ("userId", "7788")),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("post-5", result.RemotePostId);
        Assert.Equal("https://www.threads.net/@woodruff/post/abc", result.RemoteUrl);

        var container = stub.Requests.First(r => r.UriEndsWith("/7788/threads"));
        Assert.Contains("media_type=TEXT", container.Body);

        var publish = stub.Requests.First(r => r.UriEndsWith("/7788/threads_publish"));
        Assert.Contains("creation_id=container-4", publish.Body);
    }

    [Fact]
    public async Task An_image_post_hands_over_a_public_url_rather_than_bytes()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("/threads_publish", """{"id":"post-6"}""")
            .RespondJson("/7788/threads", """{"id":"container-5"}""")
            .RespondJson("fields=permalink", """{"permalink":"https://www.threads.net/@woodruff/post/img"}""");

        var result = await Platform(stub).PublishAsync(
            new PlatformPublishRequest(
                Account(SocialPlatform.Threads, "7788"),
                "With a picture",
                Image(publicUrl: "https://app.test/i/ab/cd/random.png")),
            Credentials,
            Map(("accessToken", "token"), ("userId", "7788")),
            CancellationToken.None);

        Assert.True(result.Success);

        var container = stub.Requests.First(r => r.UriEndsWith("/7788/threads"));
        Assert.Contains("media_type=IMAGE", container.Body);
        Assert.Contains("image_url=https%3A%2F%2Fapp.test%2Fi%2Fab%2Fcd%2Frandom.png", container.Body);
    }
}

public class InstagramPlatformTests
{
    private static readonly Dictionary<string, string> Credentials =
        Map((InstagramPlatform.ClientIdKey, "app-id"), (InstagramPlatform.ClientSecretKey, "app-secret"));

    private static InstagramPlatform Platform(StubHttpMessageHandler stub) =>
        new(stub.AsFactory(), Logger<InstagramPlatform>());

    [Fact]
    public async Task Connecting_finds_the_instagram_account_behind_a_facebook_page()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("fb_exchange_token", """{"access_token":"long-lived","expires_in":5184000}""")
            .RespondJson("oauth/access_token", """{"access_token":"short"}""")
            .RespondJson("me/accounts", """
                {"data":[{"id":"page-1","name":"No IG"},
                         {"id":"page-2","name":"With IG","instagram_business_account":{"id":"ig-99","username":"woodruff"}}]}
                """);

        var result = await Platform(stub).CompleteAuthorizationAsync(
            Account(SocialPlatform.Instagram),
            Credentials,
            new OAuthCallbackContext("https://app.test/callback/Instagram", "code", null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ig-99", result.RemoteAccountId);
        Assert.Equal("@woodruff", result.DisplayName);
    }

    [Fact]
    public async Task A_page_with_no_linked_account_explains_what_to_fix()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("fb_exchange_token", """{"access_token":"long-lived"}""")
            .RespondJson("oauth/access_token", """{"access_token":"short"}""")
            .RespondJson("me/accounts", """{"data":[{"id":"page-1","name":"No IG"}]}""");

        var result = await Platform(stub).CompleteAuthorizationAsync(
            Account(SocialPlatform.Instagram),
            Credentials,
            new OAuthCallbackContext("https://app.test/callback/Instagram", "code", null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Business or Creator", result.Error);
    }

    [Fact]
    public async Task Publishing_without_an_image_is_refused_before_any_call_is_made()
    {
        var stub = new StubHttpMessageHandler();

        var result = await Platform(stub).PublishAsync(
            new PlatformPublishRequest(Account(SocialPlatform.Instagram, "ig-99"), "Text only", null),
            Credentials,
            Map(("accessToken", "token"), ("igUserId", "ig-99")),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no text only post", result.Error);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Publishing_waits_for_the_container_before_publishing_it()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("/media_publish", """{"id":"ig-post-1"}""")
            .RespondJson("status_code", """{"status_code":"FINISHED"}""")
            .RespondJson("/ig-99/media", """{"id":"container-7"}""")
            .RespondJson("fields=permalink", """{"permalink":"https://www.instagram.com/p/abc/"}""");

        var result = await Platform(stub).PublishAsync(
            new PlatformPublishRequest(
                Account(SocialPlatform.Instagram, "ig-99"),
                "A caption",
                Image(publicUrl: "https://app.test/i/ab/cd/random.jpg")),
            Credentials,
            Map(("accessToken", "token"), ("igUserId", "ig-99")),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ig-post-1", result.RemotePostId);

        var container = stub.Requests.First(r => r.UriEndsWith("/ig-99/media"));
        Assert.Contains("image_url=https%3A%2F%2Fapp.test%2Fi%2Fab%2Fcd%2Frandom.jpg", container.Body);
        Assert.Contains("caption=A+caption", container.Body);

        Assert.Contains(stub.Requests, r => r.Uri.Query.Contains("status_code"));
    }

    [Fact]
    public void Instagram_requires_an_image_on_every_post()
    {
        var capabilities = Platform(new StubHttpMessageHandler()).Capabilities;
        Assert.True(capabilities.ImageRequired);
        Assert.True(capabilities.RequiresPublicImageUrl);
        Assert.Equal(2200, capabilities.DefaultCharacterLimit);
    }
}
