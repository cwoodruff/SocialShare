namespace SocialShare.Core.Services;

/// <summary>
/// Where uploaded images live. Disk today, Blob Storage later, without the pages changing.
/// </summary>
public interface IImageStore
{
    /// <summary>Writes the bytes under <paramref name="storageKey"/> and returns when durable.</summary>
    Task SaveAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>Opens the stored bytes for reading. Throws when the key is unknown.</summary>
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Cheap probe used by the health check.</summary>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}

/// <summary>Encrypts and decrypts anything secret before it touches the database.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Returns null when the payload cannot be decrypted, for example after a key loss.</summary>
    string? Unprotect(string? ciphertext);
}

/// <summary>Sends account mail. The stub logs, SendGrid sends, chosen by configuration.</summary>
public interface IAppEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken);
}
