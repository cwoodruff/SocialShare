namespace SocialShare.Core.Data;

/// <summary>
/// Who the current request belongs to. The DbContext turns this into a global query filter so a
/// forgotten Where clause cannot leak another user's rows. The background worker sets
/// <see cref="RunAsSystem"/> because it legitimately publishes on behalf of everybody.
/// </summary>
public sealed class TenantContext
{
    public Guid? UserId { get; private set; }

    public bool RunAsSystem { get; private set; }

    public void SetUser(Guid userId)
    {
        UserId = userId;
        RunAsSystem = false;
    }

    public void SetSystem()
    {
        UserId = null;
        RunAsSystem = true;
    }

    public void Clear()
    {
        UserId = null;
        RunAsSystem = false;
    }
}
