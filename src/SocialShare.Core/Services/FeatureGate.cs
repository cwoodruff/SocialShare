using SocialShare.Core.Domain;

namespace SocialShare.Core.Services;

public enum Feature
{
    /// <summary>Free organizations can schedule, but only a limited number of posts ahead.</summary>
    UnlimitedScheduledPosts = 0,

    /// <summary>Reserved for the first paid only feature. Nothing depends on it yet.</summary>
    TeamMembers = 1
}

/// <summary>
/// The single place that answers "can this organization do X". Billing is not wired up, so
/// today this just reads the plan on the organization. It exists now so that adding a paid
/// tier later is one file.
/// </summary>
public static class FeatureGate
{
    public const int FreeScheduledPostLimit = 25;

    public static bool IsEnabled(Feature feature, Plan plan) => feature switch
    {
        Feature.UnlimitedScheduledPosts => plan >= Plan.Pro,
        Feature.TeamMembers => plan >= Plan.Pro,
        _ => false
    };

    /// <summary>How many posts an organization may have queued at once, or null for no limit.</summary>
    public static int? ScheduledPostLimit(Plan plan) =>
        IsEnabled(Feature.UnlimitedScheduledPosts, plan) ? null : FreeScheduledPostLimit;
}
