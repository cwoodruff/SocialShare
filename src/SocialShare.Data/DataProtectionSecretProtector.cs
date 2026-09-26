using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using SocialShare.Core.Services;

namespace SocialShare.Data;

/// <summary>
/// Encrypts platform tokens and app passwords with ASP.NET Core Data Protection before they are
/// written to SQLite. If the key ring is lost the ciphertext is unrecoverable, which is the point:
/// a stolen database file on its own is not enough to post as anybody.
/// </summary>
public sealed class DataProtectionSecretProtector(
    IDataProtectionProvider provider,
    ILogger<DataProtectionSecretProtector> logger) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("SocialShare.SocialAccountSecrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Almost always a lost or rotated key ring. The user has to reconnect the account.
            logger.LogError(ex, "Could not decrypt a stored secret. The Data Protection key ring has probably changed.");
            return null;
        }
    }
}
