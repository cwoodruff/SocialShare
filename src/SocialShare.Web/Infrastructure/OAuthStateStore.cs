using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SocialShare.Core.Domain;

namespace SocialShare.Web.Infrastructure;

public sealed record OAuthHandoff(SocialPlatform Platform, string State, string? CodeVerifier, DateTimeOffset IssuedUtc);

/// <summary>
/// Carries the OAuth state and the PKCE verifier across the trip to the platform and back. It
/// lives in one short lived encrypted cookie rather than session state, so no server side store
/// is needed and a restart mid flow just means starting the connect again.
/// </summary>
public sealed class OAuthStateStore(IDataProtectionProvider provider, IHttpContextAccessor accessor)
{
    private const string CookieName = "ss_oauth";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector = provider.CreateProtector("SocialShare.OAuthHandoff.v1");

    public void Save(SocialPlatform platform, string state, string? codeVerifier)
    {
        var context = accessor.HttpContext ?? throw new InvalidOperationException("No HTTP context.");
        var payload = JsonSerializer.Serialize(
            new OAuthHandoff(platform, state, codeVerifier, DateTimeOffset.UtcNow), Json);

        context.Response.Cookies.Append(CookieName, _protector.Protect(payload), new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow + Lifetime,
            Path = "/app/accounts"
        });
    }

    public OAuthHandoff? Read()
    {
        var context = accessor.HttpContext;
        if (context is null || !context.Request.Cookies.TryGetValue(CookieName, out var cookie))
        {
            return null;
        }

        try
        {
            var handoff = JsonSerializer.Deserialize<OAuthHandoff>(_protector.Unprotect(cookie), Json);
            return handoff is not null && DateTimeOffset.UtcNow - handoff.IssuedUtc <= Lifetime ? handoff : null;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Clear() =>
        accessor.HttpContext?.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/app/accounts" });
}
