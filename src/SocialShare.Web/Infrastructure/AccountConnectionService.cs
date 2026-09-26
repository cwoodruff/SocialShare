using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Core.Services;

namespace SocialShare.Web.Infrastructure;

/// <summary>
/// Everything the accounts page needs: save what the user typed, start and finish an OAuth
/// round trip, test a connection and disconnect. Keeps the page model thin and keeps the
/// encryption in one place.
/// </summary>
public sealed class AccountConnectionService(
    SocialShareDbContext db,
    ISocialPlatformRegistry registry,
    SecretVault vault,
    ILogger<AccountConnectionService> logger)
{
    public Task<List<SocialAccount>> LoadAllAsync(Guid userId, CancellationToken cancellationToken) =>
        db.SocialAccounts.Where(a => a.UserId == userId).ToListAsync(cancellationToken);

    public async Task<SocialAccount> GetOrCreateAsync(
        Guid userId, Guid organizationId, SocialPlatform platform, CancellationToken cancellationToken)
    {
        var account = await db.SocialAccounts
            .FirstOrDefaultAsync(a => a.UserId == userId && a.Platform == platform, cancellationToken);

        if (account is not null)
        {
            return account;
        }

        account = new SocialAccount
        {
            UserId = userId,
            OrganizationId = organizationId,
            Platform = platform
        };

        db.SocialAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return account;
    }

    /// <summary>Which credential values are already stored, so the form can show them back.</summary>
    public IReadOnlyDictionary<string, string> ReadCredentials(SocialAccount account) =>
        vault.ReadCredentials(account);

    public async Task SaveCredentialsAsync(
        SocialAccount account,
        IReadOnlyDictionary<string, string> submitted,
        CancellationToken cancellationToken)
    {
        var platform = registry.Get(account.Platform);
        var existing = vault.ReadCredentials(account);
        var merged = new Dictionary<string, string>(existing);

        foreach (var field in platform.Capabilities.CredentialFields)
        {
            if (!submitted.TryGetValue(field.Key, out var value))
            {
                continue;
            }

            value = value.Trim();

            // A blank secret means "leave what is already stored alone", so a user can edit a
            // client id without retyping the secret.
            if (field.Kind == CredentialFieldKind.Secret && value.Length == 0)
            {
                continue;
            }

            if (value.Length == 0)
            {
                merged.Remove(field.Key);
            }
            else
            {
                merged[field.Key] = value;
            }
        }

        // Changing the identity of the app invalidates anything the old one issued.
        if (ConnectionIdentityChanged(platform, existing, merged))
        {
            vault.WriteTokens(account, null);
            account.TokenExpiresUtc = null;
            account.RemoteAccountId = null;
            account.DisplayName = null;
            account.CharacterLimitOverride = null;
        }

        vault.WriteCredentials(account, merged);
        account.Status = IsConfigured(platform, merged)
            ? account.TokensCipher is null ? AccountStatus.Configured : AccountStatus.Connected
            : AccountStatus.NotConfigured;
        account.LastError = null;
        account.UpdatedUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool ConnectionIdentityChanged(
        ISocialPlatform platform,
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        foreach (var field in platform.Capabilities.CredentialFields)
        {
            before.TryGetValue(field.Key, out var oldValue);
            after.TryGetValue(field.Key, out var newValue);

            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsConfigured(ISocialPlatform platform, IReadOnlyDictionary<string, string> credentials) =>
        platform.Capabilities.CredentialFields
            .Where(f => f.Required)
            .All(f => credentials.TryGetValue(f.Key, out var v) && !string.IsNullOrWhiteSpace(v));

    public sealed record StartResult(Uri? AuthorizationUrl, string? CodeVerifier, string? State, string? Error);

    public async Task<StartResult> StartConnectAsync(
        SocialAccount account, string redirectUri, CancellationToken cancellationToken)
    {
        var platform = registry.Get(account.Platform);
        var credentials = vault.ReadCredentials(account);

        if (!IsConfigured(platform, credentials))
        {
            return new StartResult(null, null, null, "Fill in the fields above and save before connecting.");
        }

        if (platform.Capabilities.AuthKind == PlatformAuthKind.DirectCredentials)
        {
            var connected = await ConnectDirectAsync(account, cancellationToken);
            return new StartResult(null, null, null, connected ? null : account.LastError);
        }

        var state = Platforms.Shared.OAuthHelpers.NewState();
        string? verifier = null;
        string? challenge = null;

        if (platform.Capabilities.AuthKind == PlatformAuthKind.OAuth2Pkce)
        {
            verifier = Platforms.Shared.OAuthHelpers.NewCodeVerifier();
            challenge = Platforms.Shared.OAuthHelpers.CodeChallenge(verifier);
        }

        var start = await platform.StartAuthorizationAsync(
            account, credentials, new OAuthStartContext(redirectUri, state, challenge), cancellationToken);

        if (start.UpdatedCredentials is not null)
        {
            vault.WriteCredentials(account, start.UpdatedCredentials);
            account.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (start.AuthorizationUrl is null)
        {
            return new StartResult(null, null, null, start.Error ?? "That platform could not start an authorization.");
        }

        return new StartResult(start.AuthorizationUrl, verifier, state, null);
    }

    public async Task<bool> ConnectDirectAsync(SocialAccount account, CancellationToken cancellationToken)
    {
        var platform = registry.Get(account.Platform);
        var credentials = vault.ReadCredentials(account);

        PlatformConnectResult result;
        try
        {
            result = await platform.ConnectDirectAsync(account, credentials, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connecting {Platform} threw.", account.Platform);
            result = new PlatformConnectResult(false, Error: $"{ex.GetType().Name}: {ex.Message}");
        }

        return await ApplyConnectResultAsync(account, result, cancellationToken);
    }

    public async Task<bool> CompleteOAuthAsync(
        SocialAccount account,
        string redirectUri,
        string code,
        string? codeVerifier,
        CancellationToken cancellationToken)
    {
        var platform = registry.Get(account.Platform);
        var credentials = vault.ReadCredentials(account);

        PlatformConnectResult result;
        try
        {
            result = await platform.CompleteAuthorizationAsync(
                account, credentials, new OAuthCallbackContext(redirectUri, code, codeVerifier), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Completing the {Platform} authorization threw.", account.Platform);
            result = new PlatformConnectResult(false, Error: $"{ex.GetType().Name}: {ex.Message}");
        }

        return await ApplyConnectResultAsync(account, result, cancellationToken);
    }

    private async Task<bool> ApplyConnectResultAsync(
        SocialAccount account, PlatformConnectResult result, CancellationToken cancellationToken)
    {
        account.UpdatedUtc = DateTimeOffset.UtcNow;

        if (!result.Success)
        {
            account.Status = AccountStatus.Error;
            account.LastError = result.Error;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        vault.WriteTokens(account, result.Tokens);
        account.RemoteAccountId = result.RemoteAccountId ?? account.RemoteAccountId;
        account.DisplayName = result.DisplayName ?? account.DisplayName;
        account.TokenExpiresUtc = result.TokenExpiresUtc;
        account.CharacterLimitOverride = result.CharacterLimit ?? account.CharacterLimitOverride;
        account.Status = AccountStatus.Connected;
        account.LastError = null;
        account.LastTestedUtc = DateTimeOffset.UtcNow;
        account.LastTestSucceeded = true;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<PlatformTestResult> TestAsync(SocialAccount account, CancellationToken cancellationToken)
    {
        var platform = registry.Get(account.Platform);
        var credentials = vault.ReadCredentials(account);
        var tokens = vault.ReadTokens(account);

        PlatformTestResult result;
        try
        {
            var refreshed = await platform.RefreshAsync(account, credentials, tokens, cancellationToken);
            if (refreshed is { Success: true, Tokens: not null })
            {
                vault.WriteTokens(account, refreshed.Tokens);
                account.TokenExpiresUtc = refreshed.TokenExpiresUtc;
                tokens = refreshed.Tokens;
            }
            else if (refreshed is { Success: false })
            {
                result = new PlatformTestResult(false, refreshed.Error ?? "Refreshing the stored token failed.");
                return await RecordTestAsync(account, result, cancellationToken);
            }

            result = await platform.TestAsync(account, credentials, tokens, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Testing {Platform} threw.", account.Platform);
            result = new PlatformTestResult(false, $"{ex.GetType().Name}: {ex.Message}");
        }

        return await RecordTestAsync(account, result, cancellationToken);
    }

    private async Task<PlatformTestResult> RecordTestAsync(
        SocialAccount account, PlatformTestResult result, CancellationToken cancellationToken)
    {
        account.LastTestedUtc = DateTimeOffset.UtcNow;
        account.LastTestSucceeded = result.Success;
        account.LastError = result.Success ? null : result.Message;
        account.DisplayName = result.DisplayName ?? account.DisplayName;
        account.Status = result.Success ? AccountStatus.Connected : AccountStatus.Error;
        account.UpdatedUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>Drops the tokens but keeps whatever the user typed, so reconnecting is one click.</summary>
    public async Task DisconnectAsync(SocialAccount account, CancellationToken cancellationToken)
    {
        var platform = registry.Get(account.Platform);

        try
        {
            await platform.DisconnectAsync(
                account, vault.ReadCredentials(account), vault.ReadTokens(account), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Revoking the {Platform} token failed. Clearing it locally anyway.", account.Platform);
        }

        vault.WriteTokens(account, null);
        account.TokenExpiresUtc = null;
        account.RemoteAccountId = null;
        account.LastTestedUtc = null;
        account.LastTestSucceeded = null;
        account.LastError = null;
        account.Status = AccountStatus.Configured;
        account.UpdatedUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Forgets a platform entirely, including the client id and secret.</summary>
    public async Task ForgetAsync(SocialAccount account, CancellationToken cancellationToken)
    {
        db.SocialAccounts.Remove(account);
        await db.SaveChangesAsync(cancellationToken);
    }
}
