using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace SocialShare.Web.Infrastructure;

public static class PartialResults
{
    /// <summary>
    /// PageModel.Partial builds a fresh ViewDataDictionary, so anything a handler put in
    /// ViewData is dropped before the partial renders. Every htmx handler here returns a
    /// partial that needs that context, so this copies it across.
    /// </summary>
    public static PartialViewResult PartialWithViewData(this PageModel page, string viewName) =>
        new()
        {
            ViewName = viewName,
            ViewData = new ViewDataDictionary(page.ViewData) { Model = page }
        };
}
