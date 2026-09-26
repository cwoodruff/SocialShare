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
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"socialshare-test-{Guid.NewGuid():N}.db");
    private readonly string _keyRingPath = Path.Combine(Path.GetTempPath(), $"socialshare-keys-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath}");
        builder.UseSetting("Scheduler:Enabled", "false");
        builder.UseSetting("DataProtection:KeyRingPath", _keyRingPath);
        builder.UseSetting("App:PublicBaseUrl", "http://localhost");
        builder.UseSetting("App:RequireConfirmedAccount", "false");

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
