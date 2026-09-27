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

/// <summary>
/// One account email. Both bodies are required: a transactional message with no plain text part
/// scores worse with spam filters, and the confirmation link is the whole point of the message,
/// so it needs to survive a client that will not render HTML.
/// </summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>Sends account mail. The stub logs, SendGrid sends, chosen by configuration.</summary>
public interface IAppEmailSender
{
    /// <summary>
    /// Sends the message, or throws <see cref="EmailSendException"/>. It deliberately does not
    /// return a status that a caller can ignore: a confirmation email that silently fails to
    /// send strands the account it belongs to, because signing in needs it.
    /// </summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>The provider refused the message, or could not be reached.</summary>
public sealed class EmailSendException(string message, Exception? inner = null)
    : Exception(message, inner);
