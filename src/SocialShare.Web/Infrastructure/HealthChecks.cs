using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SocialShare.Core.Data;
using SocialShare.Core.Services;

namespace SocialShare.Web.Infrastructure;

public sealed class DatabaseHealthCheck(SocialShareDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy("SQLite answered.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQLite did not answer.", ex);
        }
    }
}

public sealed class ImageStoreHealthCheck(IImageStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.IsHealthyAsync(cancellationToken)
                ? HealthCheckResult.Healthy("The image root is writable.")
                : HealthCheckResult.Unhealthy("The image root is not writable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("The image store threw.", ex);
        }
    }
}
