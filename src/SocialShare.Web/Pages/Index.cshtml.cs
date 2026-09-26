using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using SocialShare.Core.Platforms;
using SocialShare.Core.Services;

namespace SocialShare.Web.Pages;

public class IndexModel(ISocialPlatformRegistry registry, IOptions<AppOptions> options) : PageModel
{
    public IReadOnlyList<PlatformCapabilities> Platforms { get; private set; } = [];

    public bool RegistrationEnabled => options.Value.RegistrationEnabled;

    public void OnGet() => Platforms = registry.All.Select(p => p.Capabilities).ToList();
}
