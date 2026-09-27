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

    /// <summary>
    /// Require a confirmed email address before an account can sign in. On means a new account
    /// has to click the link in its confirmation email first.
    /// </summary>
    public bool RequireConfirmedAccount { get; set; } = true;

    /// <summary>
    /// Whether anybody can create an account. Off closes sign ups entirely, which also means
    /// the first account has to be created before this is turned off. See docs/operations.md.
    /// </summary>
    public bool RegistrationEnabled { get; set; }

    /// <summary>Where the docs folder is published, so the UI can link straight at it.</summary>
    public string DocsBaseUrl { get; set; } = "https://github.com/cwoodruff/SocialShare/blob/main/docs";
}

public enum EmailProvider
{
    /// <summary>Writes the message to the log instead of sending it. Fine for one person.</summary>
    Log = 0,

    /// <summary>Actually sends, through SendGrid.</summary>
    SendGrid = 1
}

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Either Log or SendGrid. An unrecognised value stops the app rather than
    /// quietly falling back, because a typo here means nobody can confirm an account.</summary>
    public string Provider { get; set; } = nameof(EmailProvider.Log);

    /// <summary>Must be an address or domain you have verified with the provider.</summary>
    public string FromAddress { get; set; } = "no-reply@socialshare.local";

    public string FromName { get; set; } = "SocialShare";

    public string? SendGridApiKey { get; set; }

    /// <summary>Where a recipient's reply goes. Optional, and usually worth setting.</summary>
    public string? ReplyToAddress { get; set; }

    /// <summary>
    /// Base URL for the SendGrid API. Leave it empty for the default. SendGrid publishes
    /// https://api.eu.sendgrid.com for EU data residency, and pointing this at a local server
    /// is how the send path gets exercised without emailing anybody.
    /// </summary>
    public string? SendGridHost { get; set; }

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Asks SendGrid to validate the request and then throw the message away. Useful for
    /// proving credentials and the sender identity work without emailing anybody.
    /// </summary>
    public bool SandboxMode { get; set; }

    /// <summary>
    /// Reads <see cref="Provider"/> as an enum, or throws when it is not one of the known
    /// values. Called at startup so a typo fails the boot rather than a registration.
    /// </summary>
    public EmailProvider ResolveProvider() =>
        Enum.TryParse<EmailProvider>(Provider, ignoreCase: true, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"Email:Provider is set to '{Provider}', which is not a provider this app knows about. "
                + $"Use one of: {string.Join(", ", Enum.GetNames<EmailProvider>())}.");
}
