using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialShare.Core.Services;

namespace SocialShare.Data;

/// <summary>
/// Files on disk under a configured root. On Azure App Service that root is under /home so the
/// uploads survive a deployment. Swapping in Blob Storage later means writing another
/// <see cref="IImageStore"/> and changing one registration.
/// </summary>
public sealed class DiskImageStore : IImageStore
{
    private readonly string _root;
    private readonly ILogger<DiskImageStore> _logger;

    public DiskImageStore(IOptions<StorageOptions> options, ILogger<DiskImageStore> logger)
    {
        _root = Path.GetFullPath(options.Value.ImageRoot);
        _logger = logger;
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var file = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);
        await content.CopyToAsync(file, cancellationToken);
        await file.FlushAsync(cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        Stream stream = new FileStream(
            ResolvePath(storageKey), FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(ResolvePath(storageKey)));

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = ResolvePath(storageKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        // Prove the root is really writable rather than just present.
        var probe = Path.Combine(_root, $".health-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(probe, "ok", cancellationToken);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The image root {Root} is not writable.", _root);
            return false;
        }
    }

    /// <summary>Keeps a crafted key from escaping the root.</summary>
    private string ResolvePath(string storageKey)
    {
        var combined = Path.GetFullPath(Path.Combine(_root, storageKey));
        if (!combined.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, _root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("That storage key points outside the image root.");
        }

        return combined;
    }
}
