namespace SocialShare.Core.Services;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Root folder for uploaded images. /home/data/uploads on Azure App Service.</summary>
    public string ImageRoot { get; set; } = "App_Data/uploads";

    public long MaxImageBytes { get; set; } = 8 * 1024 * 1024;
}

public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    public bool Enabled { get; set; } = true;

    public int PollSeconds { get; set; } = 15;

    /// <summary>How many automatic attempts a failed platform gets before it waits for me.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Backoff per attempt number. The last value repeats if attempts run past the list.</summary>
    public int[] RetryBackoffSeconds { get; set; } = [60, 300, 900];

    /// <summary>Per platform HTTP budget for one publish, including uploads.</summary>
    public int PublishTimeoutSeconds { get; set; } = 90;

    /// <summary>A post stuck in Publishing for longer than this is released for another try.</summary>
    public int StuckClaimMinutes { get; set; } = 15;
}

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Off by default. Turn it on and published posts older than the cutoff are deleted.</summary>
    public bool PurgeEnabled { get; set; }

    public int PurgePublishedAfterDays { get; set; } = 365;
}

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Absolute base URL, needed because two platforms fetch images by URL themselves.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Flip to require a confirmed email before sign in.</summary>
    public bool RequireConfirmedAccount { get; set; }

    public bool RegistrationEnabled { get; set; } = true;

    /// <summary>Where the docs folder is published, so the UI can link straight at it.</summary>
    public string DocsBaseUrl { get; set; } = "https://github.com/cwoodruff/SocialShare/blob/main/docs";
}

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Either Log or SendGrid.</summary>
    public string Provider { get; set; } = "Log";

    public string FromAddress { get; set; } = "no-reply@socialshare.local";

    public string FromName { get; set; } = "SocialShare";

    public string? SendGridApiKey { get; set; }
}
