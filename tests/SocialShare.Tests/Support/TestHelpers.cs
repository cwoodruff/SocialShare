using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;

namespace SocialShare.Tests.Support;

public static class TestHelpers
{
    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;

    public static SocialAccount Account(SocialPlatform platform, string? remoteId = null, string? displayName = null) =>
        new()
        {
            UserId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            Platform = platform,
            RemoteAccountId = remoteId,
            DisplayName = displayName,
            Status = AccountStatus.Connected,
            TokenExpiresUtc = DateTimeOffset.UtcNow.AddHours(5)
        };

    public static PublishImage Image(string contentType = "image/png", string publicUrl = "https://example.test/i/abc.png")
    {
        var bytes = Encoding.UTF8.GetBytes("not-a-real-image-but-the-bytes-travel");
        return new PublishImage(
            _ => Task.FromResult<Stream>(new MemoryStream(bytes)),
            contentType,
            bytes.Length,
            1200,
            675,
            publicUrl,
            "Alt text");
    }

    public static Dictionary<string, string> Map(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);
}
