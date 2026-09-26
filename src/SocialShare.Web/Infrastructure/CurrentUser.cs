using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;

namespace SocialShare.Web.Infrastructure;

/// <summary>
/// The signed in user, resolved once per request. Setting the tenant here is what makes the
/// global query filters on the DbContext do their job.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor accessor, TenantContext tenant)
{
    public Guid? UserId
    {
        get
        {
            var value = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public Guid RequireUserId() =>
        UserId ?? throw new InvalidOperationException("No signed in user on this request.");

    /// <summary>Called by middleware so every request is scoped before any query runs.</summary>
    public void Apply()
    {
        if (UserId is { } id)
        {
            tenant.SetUser(id);
        }
        else
        {
            tenant.Clear();
        }
    }
}

public static class UserQueries
{
    public static Task<ApplicationUser?> LoadWithOrganizationAsync(
        this SocialShareDbContext db, Guid userId, CancellationToken cancellationToken = default) =>
        db.Users.Include(u => u.Organization).FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
}
