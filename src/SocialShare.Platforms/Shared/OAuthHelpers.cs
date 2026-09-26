using System.Security.Cryptography;
using System.Text;

namespace SocialShare.Platforms.Shared;

public static class OAuthHelpers
{
    public static string NewCodeVerifier() =>
        Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string CodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string BuildQuery(IEnumerable<KeyValuePair<string, string?>> parameters) =>
        string.Join('&', parameters
            .Where(p => p.Value is not null)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));
}
