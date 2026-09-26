using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;

namespace SocialShare.Core.Services;

public sealed class SocialPlatformRegistry : ISocialPlatformRegistry
{
    private readonly Dictionary<SocialPlatform, ISocialPlatform> _byPlatform;

    public SocialPlatformRegistry(IEnumerable<ISocialPlatform> platforms)
    {
        _byPlatform = platforms.ToDictionary(p => p.Platform);

        // Display order is the enum order, which is the order Woody asked for.
        All = _byPlatform.Values.OrderBy(p => (int)p.Platform).ToList();
    }

    public IReadOnlyList<ISocialPlatform> All { get; }

    public ISocialPlatform Get(SocialPlatform platform) =>
        _byPlatform.TryGetValue(platform, out var found)
            ? found
            : throw new InvalidOperationException($"No implementation registered for {platform}.");
}
