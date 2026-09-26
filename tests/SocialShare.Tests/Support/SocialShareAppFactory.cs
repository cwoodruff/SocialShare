using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SocialShare.Core.Data;
using SocialShare.Core.Services;

namespace SocialShare.Tests.Support;

/// <summary>
/// The real application, with a throwaway SQLite file, the scheduler switched off and the image
/// store in memory. Everything else runs exactly as it does in production, including Identity,
/// the query filters and the authorization policies.
/// </summary>
public sealed class SocialShareAppFactory : WebApplicationFactory<Program>
{
    /// <summary>Overrides applied after the defaults, so a test can open or close the gate.</summary>
    public Dictionary<string, string> Settings { get; } = [];

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"socialshare-test-{Guid.NewGuid():N}.db");
    private readonly string _keyRingPath = Path.Combine(Path.GetTempPath(), $"socialshare-keys-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath}");
        builder.UseSetting("Scheduler:Enabled", "false");
        builder.UseSetting("DataProtection:KeyRingPath", _keyRingPath);
        builder.UseSetting("App:PublicBaseUrl", "http://localhost");
        // These two tests are about tenant isolation, not the sign up gate, so the gate is
        // opened here explicitly. AccountGateTests covers the shipped defaults instead.
        builder.UseSetting("App:RegistrationEnabled", "true");
        builder.UseSetting("App:RequireConfirmedAccount", "false");

        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IImageStore>();
            services.AddSingleton<IImageStore, InMemoryImageStore>();

            // Nothing in these tests should reach a real social network.
            services.RemoveAll<IHostedService>();
        });
    }

    public HttpClient NewBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    /// <summary>Stands in for clicking the link in a confirmation email.</summary>
    public async Task ConfirmEmailAsync(string email)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<SocialShare.Core.Domain.ApplicationUser>>();

        var user = await users.FindByEmailAsync(email)
                   ?? throw new InvalidOperationException($"No account for {email}.");

        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var result = await users.ConfirmEmailAsync(user, token);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        }
    }

    public async Task<T> UseDatabaseAsync<T>(Func<SocialShareDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.SetSystem();
        return await work(scope.ServiceProvider.GetRequiredService<SocialShareDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, _databasePath + "-shm", _databasePath + "-wal" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A locked temp file is not worth failing a test run over.
            }
        }

        try
        {
            if (Directory.Exists(_keyRingPath))
            {
                Directory.Delete(_keyRingPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
