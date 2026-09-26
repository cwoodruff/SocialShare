using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SocialShare.Core.Data;

namespace SocialShare.Data;

/// <summary>
/// Only used by dotnet ef at design time. Migrations live in this project, the DbContext lives
/// in Core, so the tooling needs a way to build one without starting the web host.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SocialShareDbContext>
{
    public SocialShareDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SocialShareDbContext>()
            .UseSqlite(
                "Data Source=socialshare-design.db",
                sqlite => sqlite.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.FullName))
            .Options;

        return new SocialShareDbContext(options, new TenantContext());
    }
}
