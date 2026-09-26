using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;

namespace SocialShare.Tests.Support;

/// <summary>An ISocialPlatform whose answers the test decides, so the publisher can be driven.</summary>
public sealed class FakePlatform(SocialPlatform platform) : ISocialPlatform
{
    public SocialPlatform Platform { get; } = platform;

    public PlatformCapabilities Capabilities { get; set; } = new()
    {
        Platform = platform,
        DisplayName = platform.ToString(),
        AuthKind = PlatformAuthKind.DirectCredentials,
        DefaultCharacterLimit = 300,
        RecommendedImage = "Anything",
        SetupDocAnchor = platform.ToString().ToLowerInvariant(),
        Summary = "A fake",
        CredentialFields = []
    };

    public Func<PlatformPublishRequest, PlatformPublishResult> OnPublish { get; set; } =
        _ => new PlatformPublishResult(true, "remote-1", "https://example.test/p/1");

    public PlatformConnectResult? RefreshResult { get; set; }

    public List<PlatformPublishRequest> Published { get; } = [];

    public Task<PlatformAuthorizationStart> StartAuthorizationAsync(
        SocialAccount account, IReadOnlyDictionary<string, string> credentials,
        OAuthStartContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new PlatformAuthorizationStart(new Uri("https://example.test/authorize")));

    public Task<PlatformConnectResult> CompleteAuthorizationAsync(
        SocialAccount account, IReadOnlyDictionary<string, string> credentials,
        OAuthCallbackContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new PlatformConnectResult(true, "@fake", "remote-account"));

    public Task<PlatformConnectResult> ConnectDirectAsync(
        SocialAccount account, IReadOnlyDictionary<string, string> credentials, CancellationToken cancellationToken) =>
        Task.FromResult(new PlatformConnectResult(true, "@fake", "remote-account",
            new Dictionary<string, string> { ["accessToken"] = "token" }));

    public Task<PlatformConnectResult?> RefreshAsync(
        SocialAccount account, IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens, CancellationToken cancellationToken) =>
        Task.FromResult(RefreshResult);

    public Task<PlatformTestResult> TestAsync(
        SocialAccount account, IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens, CancellationToken cancellationToken) =>
        Task.FromResult(new PlatformTestResult(true, "Fine."));

    public Task<PlatformPublishResult> PublishAsync(
        PlatformPublishRequest request, IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens, CancellationToken cancellationToken)
    {
        Published.Add(request);
        return Task.FromResult(OnPublish(request));
    }

    public Task DisconnectAsync(
        SocialAccount account, IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class PassThroughProtector : Core.Services.ISecretProtector
{
    public string Protect(string plaintext) => "plain:" + plaintext;

    public string? Unprotect(string? ciphertext) =>
        ciphertext is null ? null : ciphertext.StartsWith("plain:", StringComparison.Ordinal) ? ciphertext[6..] : null;
}

public sealed class InMemoryImageStore : Core.Services.IImageStore
{
    private readonly Dictionary<string, byte[]> _files = [];

    public Task SaveAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        _files[storageKey] = buffer.ToArray();
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
        _files.TryGetValue(storageKey, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes))
            : throw new FileNotFoundException(storageKey);

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken) =>
        Task.FromResult(_files.ContainsKey(storageKey));

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        _files.Remove(storageKey);
        return Task.CompletedTask;
    }

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}
