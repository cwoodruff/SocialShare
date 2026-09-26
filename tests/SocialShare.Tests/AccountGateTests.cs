using System.Net;
using Microsoft.EntityFrameworkCore;
using SocialShare.Tests.Support;

namespace SocialShare.Tests;

/// <summary>
/// The two flags that decide who can get in at all. Both ship on, so both are worth proving
/// rather than trusting a configuration value to be read in the right place.
/// </summary>
public class AccountGateTests
{
    [Fact]
    public async Task With_registration_closed_nobody_can_create_an_account()
    {
        using var app = new SocialShareAppFactory();
        app.Settings["App:RegistrationEnabled"] = "false";

        var visitor = app.NewBrowser();
        var email = $"nope-{Guid.NewGuid():N}@example.test";

        var page = await visitor.GetStringAtAsync("/register");
        Assert.Contains("Registration is closed on this instance", page);

        // The form is not rendered, but posting at it anyway must not work either.
        using var response = await visitor.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["Input.DisplayName"] = "Sneaky",
            ["Input.Email"] = email,
            ["Input.Password"] = "CorrectHorse99",
            ["Input.ConfirmPassword"] = "CorrectHorse99",
            ["Input.TimeZoneId"] = "America/Detroit"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await app.UseDatabaseAsync(db => db.Users.AnyAsync(u => u.Email == email)));
    }

    [Fact]
    public async Task With_registration_closed_the_home_page_stops_offering_it()
    {
        using var app = new SocialShareAppFactory();
        app.Settings["App:RegistrationEnabled"] = "false";

        var home = await app.NewBrowser().GetStringAtAsync("/");

        Assert.DoesNotContain("Create account", home);
        Assert.Contains("Sign in", home);
    }

    [Fact]
    public async Task An_unconfirmed_account_cannot_sign_in_and_is_pointed_at_the_resend_page()
    {
        using var app = new SocialShareAppFactory();
        app.Settings["App:RequireConfirmedAccount"] = "true";

        var browser = app.NewBrowser();
        var email = $"unconfirmed-{Guid.NewGuid():N}@example.test";

        // Registration succeeds but lands on the check your email page rather than signing in.
        using (var registered = await browser.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["Input.DisplayName"] = "Pending",
            ["Input.Email"] = email,
            ["Input.Password"] = "CorrectHorse99",
            ["Input.ConfirmPassword"] = "CorrectHorse99",
            ["Input.TimeZoneId"] = "America/Detroit"
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
            Assert.Contains("check-your-email", registered.Headers.Location!.ToString());
        }

        Assert.True(await app.UseDatabaseAsync(db => db.Users.AnyAsync(u => u.Email == email)));

        using var signIn = await browser.PostFormAsync("/login", new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = "CorrectHorse99",
            ["Input.RememberMe"] = "true"
        });

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var html = await signIn.Content.ReadAsStringAsync();
        Assert.Contains("has not confirmed its email address", html);
        Assert.Contains("/resend-confirmation", html);

        // And the app is still shut to them.
        using var app_ = await browser.GetAsync("/app");
        Assert.Equal(HttpStatusCode.Redirect, app_.StatusCode);
        Assert.Contains("/login", app_.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Confirming_the_email_lets_that_account_in()
    {
        using var app = new SocialShareAppFactory();
        app.Settings["App:RequireConfirmedAccount"] = "true";

        var browser = app.NewBrowser();
        var email = $"confirming-{Guid.NewGuid():N}@example.test";

        using (await browser.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["Input.DisplayName"] = "Confirming",
            ["Input.Email"] = email,
            ["Input.Password"] = "CorrectHorse99",
            ["Input.ConfirmPassword"] = "CorrectHorse99",
            ["Input.TimeZoneId"] = "America/Detroit"
        })) { }

        // Stand in for clicking the link in the email.
        await app.ConfirmEmailAsync(email);

        using var signIn = await browser.PostFormAsync("/login", new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = "CorrectHorse99",
            ["Input.RememberMe"] = "true"
        });

        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/app", signIn.Headers.Location!.ToString());

        Assert.Contains("Hi Confirming", await browser.GetStringAtAsync("/app"));
    }

    [Fact]
    public async Task The_resend_page_says_the_same_thing_whether_or_not_the_account_exists()
    {
        using var app = new SocialShareAppFactory();
        app.Settings["App:RequireConfirmedAccount"] = "true";

        var browser = app.NewBrowser();
        var real = $"real-{Guid.NewGuid():N}@example.test";

        using (await browser.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["Input.DisplayName"] = "Real",
            ["Input.Email"] = real,
            ["Input.Password"] = "CorrectHorse99",
            ["Input.ConfirmPassword"] = "CorrectHorse99",
            ["Input.TimeZoneId"] = "America/Detroit"
        })) { }

        var forReal = await ResendAsync(browser, real);
        var forNobody = await ResendAsync(browser, $"ghost-{Guid.NewGuid():N}@example.test");

        // Identical once the per request antiforgery token is taken out, so the page cannot be
        // used to work out which addresses have an account or which are already confirmed.
        Assert.Equal(WithoutToken(forNobody), WithoutToken(forReal));
        Assert.Contains("a new link is on", forReal);
    }

    /// <summary>
    /// Antiforgery tokens are regenerated per request and appear twice, in the hidden form field
    /// and in the hx-headers attribute on the body. They are the only thing that legitimately
    /// differs between two renders of the same page.
    /// </summary>
    private static string WithoutToken(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, @"CfDJ8[A-Za-z0-9_\-]+", "TOKEN");

    private static async Task<string> ResendAsync(HttpClient browser, string email)
    {
        using var response = await browser.PostFormAsync(
            "/resend-confirmation", new Dictionary<string, string> { ["Email"] = email });

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
