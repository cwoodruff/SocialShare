using System.Net;
using System.Text.RegularExpressions;

namespace SocialShare.Tests.Support;

/// <summary>Drives the real pages the way a browser would, tokens and all.</summary>
public static partial class BrowserExtensions
{
    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex FormToken();

    [GeneratedRegex("""hx-headers='\{&quot;RequestVerificationToken&quot;: &quot;([^&]+)&quot;\}'""")]
    private static partial Regex HeaderToken();

    public static async Task<string> GetStringAtAsync(this HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await response.Content.ReadAsStringAsync();
    }

    public static async Task<string> AntiforgeryTokenAsync(this HttpClient client, string url)
    {
        var html = await client.GetStringAtAsync(url);

        var form = FormToken().Match(html);
        if (form.Success)
        {
            return form.Groups[1].Value;
        }

        var header = HeaderToken().Match(html);
        return header.Success
            ? header.Groups[1].Value
            : throw new InvalidOperationException($"No antiforgery token on {url}.");
    }

    public static async Task<HttpResponseMessage> PostFormAsync(
        this HttpClient client,
        string url,
        Dictionary<string, string> fields,
        string? tokenFromUrl = null,
        bool asHtmx = false)
    {
        fields["__RequestVerificationToken"] = await client.AntiforgeryTokenAsync(tokenFromUrl ?? url);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields)
        };

        if (asHtmx)
        {
            request.Headers.TryAddWithoutValidation("HX-Request", "true");
        }

        return await client.SendAsync(request);
    }

    public static async Task RegisterAsync(this HttpClient client, string email, string password = "CorrectHorse99")
    {
        using var response = await client.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["Input.DisplayName"] = email.Split('@')[0],
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password,
            ["Input.TimeZoneId"] = "America/Detroit"
        });

        if (response.StatusCode != HttpStatusCode.Redirect)
        {
            throw new InvalidOperationException(
                $"Registering {email} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }
}
