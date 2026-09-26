using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Text;

namespace SocialShare.Core.Services;

public sealed record ImageUploadResult(StoredImage? Image, string? Error)
{
    public bool Success => Image is not null;
}

public sealed class ImageService(
    SocialShareDbContext db,
    IImageStore store,
    IOptions<StorageOptions> storageOptions)
{
    private readonly StorageOptions _options = storageOptions.Value;

    public long MaxBytes => _options.MaxImageBytes;

    public async Task<ImageUploadResult> UploadAsync(
        Guid userId,
        Guid organizationId,
        string fileName,
        Stream content,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        if (declaredLength <= 0)
        {
            return new ImageUploadResult(null, "That file is empty.");
        }

        if (declaredLength > _options.MaxImageBytes)
        {
            var mb = _options.MaxImageBytes / 1024d / 1024d;
            return new ImageUploadResult(null, $"That file is larger than the {mb:0.#} MB limit.");
        }

        // Buffer to memory so the content type can be checked from the real bytes before anything
        // is written to disk. The size cap above keeps this bounded.
        using var buffer = new MemoryStream(capacity: (int)Math.Min(declaredLength, _options.MaxImageBytes));
        await content.CopyToAsync(buffer, cancellationToken);

        if (buffer.Length > _options.MaxImageBytes)
        {
            var mb = _options.MaxImageBytes / 1024d / 1024d;
            return new ImageUploadResult(null, $"That file is larger than the {mb:0.#} MB limit.");
        }

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var info = ImageInspector.Inspect(bytes);
        if (info is null)
        {
            return new ImageUploadResult(null, "That file is not a JPEG, PNG or WebP image.");
        }

        var key = NewStorageKey(info.ContentType);
        buffer.Position = 0;
        await store.SaveAsync(key, buffer, info.ContentType, cancellationToken);

        var image = new StoredImage
        {
            UserId = userId,
            OrganizationId = organizationId,
            OriginalFileName = Path.GetFileName(fileName),
            StorageKey = key,
            ContentType = info.ContentType,
            ByteSize = buffer.Length,
            Width = info.Width,
            Height = info.Height
        };

        db.StoredImages.Add(image);
        await db.SaveChangesAsync(cancellationToken);

        return new ImageUploadResult(image, null);
    }

    public Task<StoredImage?> FindByKeyAsync(string storageKey, CancellationToken cancellationToken) =>
        db.StoredImages.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.StorageKey == storageKey, cancellationToken);

    public Task<Stream> OpenAsync(StoredImage image, CancellationToken cancellationToken) =>
        store.OpenReadAsync(image.StorageKey, cancellationToken);

    /// <summary>
    /// Random name with no relationship to the user or the file. Two platforms fetch images from
    /// a public URL, so the key is the only thing standing between an image and the internet.
    /// </summary>
    private static string NewStorageKey(string contentType)
    {
        var name = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var extension = contentType switch
        {
            ImageInspector.Png => ".png",
            ImageInspector.WebP => ".webp",
            _ => ".jpg"
        };

        // Two levels of fan out so a single directory never holds every upload.
        return $"{name[..2]}/{name[2..4]}/{name}{extension}";
    }
}
