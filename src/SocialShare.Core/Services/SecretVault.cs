using System.Text.Json;
using SocialShare.Core.Domain;

namespace SocialShare.Core.Services;

/// <summary>
/// Reads and writes the two encrypted blobs on a <see cref="SocialAccount"/>. Everything the
/// user typed and everything a platform handed back goes through here, so nothing secret is
/// ever written to SQLite in the clear.
/// </summary>
public sealed class SecretVault(ISecretProtector protector)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyDictionary<string, string> ReadCredentials(SocialAccount account) =>
        Read(account.CredentialsCipher);

    public IReadOnlyDictionary<string, string> ReadTokens(SocialAccount account) =>
        Read(account.TokensCipher);

    public void WriteCredentials(SocialAccount account, IReadOnlyDictionary<string, string> values) =>
        account.CredentialsCipher = Write(values);

    public void WriteTokens(SocialAccount account, IReadOnlyDictionary<string, string>? values) =>
        account.TokensCipher = values is null ? null : Write(values);

    private Dictionary<string, string> Read(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher))
        {
            return [];
        }

        var plain = protector.Unprotect(cipher);
        if (plain is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private string Write(IReadOnlyDictionary<string, string> values) =>
        protector.Protect(JsonSerializer.Serialize(values, Json));
}
