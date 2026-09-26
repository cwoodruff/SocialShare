using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Domain;
using SocialShare.Tests.Support;

namespace SocialShare.Tests;

/// <summary>
/// The test that has to keep passing if this ever becomes a product. Two real accounts, two real
/// browser sessions, and nothing of A's reachable from B.
/// </summary>
public class TenantIsolationTests : IClassFixture<SocialShareAppFactory>
{
    private readonly SocialShareAppFactory _app;

    public TenantIsolationTests(SocialShareAppFactory app) => _app = app;

    [Fact]
    public async Task User_b_cannot_see_or_touch_anything_belonging_to_user_a()
    {
        var alice = _app.NewBrowser();
        var bob = _app.NewBrowser();

        var aliceEmail = $"alice-{Guid.NewGuid():N}@example.test";
        var bobEmail = $"bob-{Guid.NewGuid():N}@example.test";

        await alice.RegisterAsync(aliceEmail);
        await bob.RegisterAsync(bobEmail);

        // Alice sets up an account, uploads an image and saves a post.
        await SaveBlueskyCredentialsAsync(alice, "alice.bsky.social", "alice-app-password");
        var aliceImage = await UploadImageAsync(alice);
        var alicePostId = await SaveDraftAsync(alice, "Alice's private draft", aliceImage);

        // Bob does the same so the isolation is not just an empty database.
        await SaveBlueskyCredentialsAsync(bob, "bob.bsky.social", "bob-app-password");
        var bobPostId = await SaveDraftAsync(bob, "Bob's own draft", null);

        // Posts: Bob's list shows only his, and Alice's post is not reachable by URL.
        var bobList = await bob.GetStringAtAsync("/app/posts");
        Assert.Contains("Bob&#x27;s own draft", bobList);
        Assert.DoesNotContain("Alice", bobList);

        using (var response = await bob.GetAsync($"/app/posts/{alicePostId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var response = await bob.GetAsync($"/app/posts/{alicePostId}/edit"))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // Deleting someone else's post is a not found, not a delete.
        using (var response = await PostWithHeaderTokenAsync(
                   bob, $"/app/posts/{alicePostId}?handler=Delete", $"/app/posts/{bobPostId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.True(await _app.UseDatabaseAsync(db =>
            db.Posts.IgnoreQueryFilters().AnyAsync(p => p.Id == alicePostId)));

        // Accounts: Bob's accounts page shows Bob's handle and never Alice's.
        var bobAccounts = await bob.GetStringAtAsync("/app/accounts");
        Assert.Contains("bob.bsky.social", bobAccounts);
        Assert.DoesNotContain("alice.bsky.social", bobAccounts);

        // Bob cannot disconnect or forget Alice's account, because the row he would act on is
        // resolved through his own tenant and simply is not there.
        using (var response = await PostWithHeaderTokenAsync(
                   bob, "/app/accounts?handler=Forget&platform=Bluesky", "/app/accounts"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.True(await _app.UseDatabaseAsync(db => db.SocialAccounts
            .IgnoreQueryFilters()
            .AnyAsync(a => a.Platform == SocialPlatform.Bluesky && a.DisplayName == null
                           && a.CredentialsCipher != null
                           && a.UserId == db.Users.First(u => u.Email == aliceEmail).Id)));

        // Images: the store is deliberately public by URL, but the metadata is not shared and
        // the compose screen never hands Bob a reference to Alice's upload.
        var bobCompose = await bob.GetStringAtAsync("/app/posts/new");
        Assert.DoesNotContain(aliceImage.StorageKey, bobCompose);

        var aliceDetail = await alice.GetStringAtAsync($"/app/posts/{alicePostId}");
        Assert.Contains("Alice&#x27;s private draft", aliceDetail);

        // The admin view is the one place that crosses tenants, and it is closed to both of them.
        using (var response = await bob.GetAsync("/admin"))
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("access-denied", response.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task Signed_out_visitors_are_sent_to_sign_in_rather_than_shown_anything()
    {
        var stranger = _app.NewBrowser();

        foreach (var path in new[] { "/app", "/app/posts", "/app/posts/new", "/app/accounts", "/app/settings", "/admin" })
        {
            using var response = await stranger.GetAsync(path);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/login", response.Headers.Location!.ToString());
        }
    }

    private static async Task SaveBlueskyCredentialsAsync(HttpClient browser, string handle, string appPassword)
    {
        using var response = await browser.PostFormAsync(
            "/app/accounts?handler=Save&platform=Bluesky",
            new Dictionary<string, string>
            {
                ["handle"] = handle,
                ["appPassword"] = appPassword,
                ["pdsHost"] = "https://bsky.social"
            },
            tokenFromUrl: "/app/accounts",
            asHtmx: true);

        response.EnsureSuccessStatusCode();
    }

    private static async Task<StoredImage> UploadImageAsync(HttpClient browser)
    {
        var token = await browser.AntiforgeryTokenAsync("/app/posts/new");

        using var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("Bluesky"), "Targets[1].Platform" }
        };

        var file = new ByteArrayContent(TestImages.Png(1200, 675));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "shot.png");

        using var response = await browser.PostAsync("/app/posts/new?handler=UploadImage&index=1", content);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        var key = Regex.Match(html, @"src=""/i/([^""]+)""").Groups[1].Value;
        var id = Regex.Match(html, @"name=""Targets\[1\].ImageId"" value=""([0-9a-f-]+)""").Groups[1].Value;

        Assert.NotEmpty(key);

        return new StoredImage { Id = Guid.Parse(id), StorageKey = key };
    }

    private static async Task<Guid> SaveDraftAsync(HttpClient browser, string title, StoredImage? image)
    {
        var fields = new Dictionary<string, string>
        {
            ["Title"] = title,
            ["MasterBody"] = title,
            ["Targets[0].Platform"] = "LinkedIn",
            ["Targets[1].Platform"] = "Bluesky",
            ["Targets[2].Platform"] = "Mastodon",
            ["Targets[3].Platform"] = "X",
            ["Targets[4].Platform"] = "Threads",
            ["Targets[5].Platform"] = "Instagram",
            ["Targets[1].Enabled"] = "true",
            ["Targets[1].Body"] = title
        };

        if (image is not null)
        {
            fields["Targets[1].ImageId"] = image.Id.ToString();
        }

        using var response = await browser.PostFormAsync(
            "/app/posts/new?handler=Save", fields, tokenFromUrl: "/app/posts/new");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!.ToString();
        return Guid.Parse(location[(location.LastIndexOf('/') + 1)..]);
    }

    private static async Task<HttpResponseMessage> PostWithHeaderTokenAsync(
        HttpClient browser, string url, string tokenFromUrl)
    {
        var token = await browser.AntiforgeryTokenAsync(tokenFromUrl);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token
            })
        };

        request.Headers.TryAddWithoutValidation("HX-Request", "true");
        return await browser.SendAsync(request);
    }
}
