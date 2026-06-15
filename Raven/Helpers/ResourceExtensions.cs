using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace Raven.Helpers;

public static class ResourceExtensions
{
    private static readonly ResourceManager _resourceManager = new();
    private static readonly ResourceMap _resourceMap = _resourceManager.MainResourceMap.GetSubtree("Resources");

    // The default ResourceContext resolves against the *system* language, not the app's
    // PrimaryLanguageOverride. So a context whose Language qualifier is pinned to the override is
    // cached and reused; without this, GetLocalized() strings ignore the in-app language setting
    // (XAML x:Uid honours the override, code-behind lookups would not). Rebuilt when it changes
    private static readonly object _contextLock = new();
    private static ResourceContext? _context;
    private static string? _contextLanguage;

    public static string GetLocalized(this string resourceKey)
    {
        try
        {
            return _resourceMap.GetValue(resourceKey, GetContext())?.ValueAsString ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ResourceContext GetContext()
    {
        var language = ApplicationLanguages.PrimaryLanguageOverride;

        lock (_contextLock)
        {
            if (_context is null || _contextLanguage != language)
            {
                var context = _resourceManager.CreateResourceContext();
                if (!string.IsNullOrEmpty(language))
                    context.QualifierValues["Language"] = language;

                _context = context;
                _contextLanguage = language;
            }

            return _context;
        }
    }
}
