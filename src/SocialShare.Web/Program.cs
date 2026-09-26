using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SocialShare.Core.Data;
using SocialShare.Core.Domain;
using SocialShare.Core.Platforms;
using SocialShare.Core.Services;
using SocialShare.Data;
using SocialShare.Platforms.Bluesky;
using SocialShare.Platforms.Instagram;
using SocialShare.Platforms.LinkedIn;
using SocialShare.Platforms.Mastodon;
using SocialShare.Platforms.Shared;
using SocialShare.Platforms.Threads;
using SocialShare.Platforms.X;
using SocialShare.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<SchedulerOptions>(builder.Configuration.GetSection(SchedulerOptions.SectionName));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));

// One SQLite file holds Identity, tenants, accounts, posts, image metadata and publish logs.
builder.Services.AddDbContext<SocialShareDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("Default")
            ?? "Data Source=App_Data/socialshare.db",
        sqlite => sqlite.MigrationsAssembly("SocialShare.Data")));

builder.Services.AddScoped<TenantContext>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();

// The key ring has to outlive a restart or every stored token becomes unreadable. On App
// Service /home is the only writable path that survives deployments.
var keyRing = builder.Configuration["DataProtection:KeyRingPath"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("SocialShare");
if (!string.IsNullOrWhiteSpace(keyRing))
{
    Directory.CreateDirectory(keyRing);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyRing));
}

builder.Services.AddScoped<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddScoped<SecretVault>();
builder.Services.AddSingleton<IImageStore, DiskImageStore>();
builder.Services.AddScoped<ImageService>();
builder.Services.AddScoped<PublishingService>();
builder.Services.AddScoped<AccountConnectionService>();
builder.Services.AddScoped<OAuthStateStore>();

// htmx posts from buttons that are not inside a form, so the token travels in a header that
// the layout puts on the body element once per page.
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");

var emailOptions = builder.Configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();
if (string.Equals(emailOptions.Provider, "SendGrid", StringComparison.OrdinalIgnoreCase)
    && !string.IsNullOrWhiteSpace(emailOptions.SendGridApiKey))
{
    builder.Services.AddScoped<IAppEmailSender, SendGridEmailSender>();
}
else
{
    builder.Services.AddScoped<IAppEmailSender, LogEmailSender>();
}

builder.Services.AddHttpClient(PlatformBase.HttpClientName, client =>
{
    // Every platform call is bounded. The publisher applies a tighter budget on top of this.
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SocialShare/1.0 (+https://github.com/cwoodruff/SocialShare)");
});

builder.Services.AddScoped<ISocialPlatform, LinkedInPlatform>();
builder.Services.AddScoped<ISocialPlatform, BlueskyPlatform>();
builder.Services.AddScoped<ISocialPlatform, MastodonPlatform>();
builder.Services.AddScoped<ISocialPlatform, XPlatform>();
builder.Services.AddScoped<ISocialPlatform, ThreadsPlatform>();
builder.Services.AddScoped<ISocialPlatform, InstagramPlatform>();
builder.Services.AddScoped<ISocialPlatformRegistry, SocialPlatformRegistry>();

builder.Services.AddHostedService<SchedulerBackgroundService>();

var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();

builder.Services
    .AddIdentity<ApplicationUser, ApplicationRole>(options =>
    {
        options.SignIn.RequireConfirmedAccount = appOptions.RequireConfirmedAccount;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 10;
    })
    .AddEntityFrameworkStores<SocialShareDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.AccessDeniedPath = "/access-denied";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, policy => policy.RequireRole(Roles.Admin));

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/App");
    options.Conventions.AuthorizeFolder("/Admin", Policies.Admin);

    // One compose page, two URLs: a new post and an edit of an existing one.
    options.Conventions.AddPageRoute("/App/Posts/Compose", "/app/posts/{id:guid}/edit");
});

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<ImageStoreHealthCheck>("images", tags: ["ready"]);

var app = builder.Build();

// Migrate before serving anything. A failure here stops the app rather than letting it run
// against a half migrated database.
await MigrateAsync(app);

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();

// Scope the DbContext filters to whoever is signed in, before any page runs a query.
app.Use(async (context, next) =>
{
    context.RequestServices.GetRequiredService<CurrentUser>().Apply();
    await next();
});

app.UseAuthorization();

app.MapRazorPages();

app.MapHealthChecks("/health");

// Threads and Instagram fetch images from a URL rather than accepting an upload, so stored
// images are served publicly under an unguessable random key. See docs/operations.md.
app.MapGet("/i/{**storageKey}", async (
    string storageKey,
    ImageService images,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var image = await images.FindByKeyAsync(storageKey, cancellationToken);
    if (image is null)
    {
        return Results.NotFound();
    }

    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";

    var stream = await images.OpenAsync(image, cancellationToken);
    return Results.Stream(stream, image.ContentType, enableRangeProcessing: true);
}).AllowAnonymous();

app.Run();

static async Task MigrateAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystem();

    try
    {
        var db = scope.ServiceProvider.GetRequiredService<SocialShareDbContext>();

        var connectionString = db.Database.GetConnectionString();
        if (connectionString is not null)
        {
            var builderForPath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
            var directory = Path.GetDirectoryName(Path.GetFullPath(builderForPath.DataSource));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        await db.Database.MigrateAsync();
        await SeedAsync(scope.ServiceProvider, app.Configuration);
        logger.LogInformation("Database is up to date.");
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Database migration failed. Refusing to start against a half migrated database.");
        throw;
    }
}

static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
{
    var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
    if (!await roles.RoleExistsAsync(Roles.Admin))
    {
        await roles.CreateAsync(new ApplicationRole(Roles.Admin));
    }

    // Anyone listed in configuration gets the admin role on the next start. This is how the
    // first admin exists at all, since there is no bootstrap screen.
    var adminEmails = configuration.GetSection("App:AdminEmails").Get<string[]>() ?? [];
    if (adminEmails.Length == 0)
    {
        return;
    }

    var users = services.GetRequiredService<UserManager<ApplicationUser>>();
    foreach (var email in adminEmails.Where(e => !string.IsNullOrWhiteSpace(e)))
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not null && !await users.IsInRoleAsync(user, Roles.Admin))
        {
            await users.AddToRoleAsync(user, Roles.Admin);
        }
    }
}

public static class Roles
{
    public const string Admin = "Admin";
}

public static class Policies
{
    public const string Admin = "AdminOnly";
}

/// <summary>Named so the integration tests can reach the host.</summary>
public partial class Program;
