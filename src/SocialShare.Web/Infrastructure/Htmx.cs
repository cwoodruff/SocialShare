namespace SocialShare.Web.Infrastructure;

/// <summary>Small helpers around the headers htmx sends and reads.</summary>
public static class Htmx
{
    public static bool IsHtmx(this HttpRequest request) =>
        request.Headers.ContainsKey("HX-Request");

    /// <summary>Tells htmx to do a full page navigation instead of swapping the response in.</summary>
    public static void HtmxRedirect(this HttpResponse response, string url) =>
        response.Headers["HX-Redirect"] = url;

    /// <summary>Tells htmx to reload the page. Used after an action changes more than one region.</summary>
    public static void HtmxRefresh(this HttpResponse response) =>
        response.Headers["HX-Refresh"] = "true";

    /// <summary>Fires a client side event that other parts of the page can listen for.</summary>
    public static void HtmxTrigger(this HttpResponse response, string eventName) =>
        response.Headers["HX-Trigger"] = eventName;
}
