using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;

namespace SocialShare.Platforms.Shared;

/// <summary>
/// Shared plumbing for the six implementations: one HttpClient, JSON helpers and readable
/// error messages built from whatever the platform actually returned.
/// </summary>
public abstract class PlatformBase(IHttpClientFactory httpClientFactory, ILogger logger) : ISocialPlatform
{
    public const string HttpClientName = "social";

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected ILogger Logger { get; } = logger;

    protected HttpClient NewClient() => httpClientFactory.CreateClient(HttpClientName);

    public abstract SocialPlatform Platform { get; }

    public abstract PlatformCapabilities Capabilities { get; }

    public virtual Task<PlatformAuthorizationStart> StartAuthorizationAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        OAuthStartContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PlatformAuthorizationStart(
            null, null, $"{Capabilities.DisplayName} does not use a browser authorization flow."));

    public virtual Task<PlatformConnectResult> CompleteAuthorizationAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        OAuthCallbackContext context,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Capabilities.DisplayName} does not use a browser authorization flow.");

    public virtual Task<PlatformConnectResult> ConnectDirectAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Capabilities.DisplayName} connects through the browser, not directly.");

    public virtual Task<PlatformConnectResult?> RefreshAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken) =>
        Task.FromResult<PlatformConnectResult?>(null);

    public abstract Task<PlatformTestResult> TestAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken);

    public abstract Task<PlatformPublishResult> PublishAsync(
        PlatformPublishRequest request,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken);

    public virtual Task DisconnectAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    protected static string? Value(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    protected static string Require(IReadOnlyDictionary<string, string> map, string key, string label) =>
        Value(map, key) ?? throw new PlatformConfigurationException($"{label} is missing. Add it on the accounts page.");

    /// <summary>Reads a failed response into something worth showing in the UI.</summary>
    protected static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            body = $"(could not read the response body: {ex.Message})";
        }

        if (body.Length > 1500)
        {
            body = body[..1500] + "...";
        }

        return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {body}".Trim();
    }

    protected static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return doc;
    }

    protected static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>Thrown when the user has not filled in something the platform needs.</summary>
public sealed class PlatformConfigurationException(string message) : Exception(message);
