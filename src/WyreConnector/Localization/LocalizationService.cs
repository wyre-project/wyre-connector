using System.Globalization;
using Microsoft.Extensions.Localization;

namespace WyreConnector.Localization;

public class LocalizationService
{
    private readonly IStringLocalizer<LocalizationService> _localizer;

    public LocalizationService(IStringLocalizer<LocalizationService> localizer)
    {
        _localizer = localizer;
    }

    public static void InitializeLanguage(string? preferredLanguage = null)
    {
        CultureInfo culture;

        if (!string.IsNullOrEmpty(preferredLanguage))
        {
            try
            {
                culture = new CultureInfo(preferredLanguage);
            }
            catch (CultureNotFoundException)
            {
                // Fallback to autodetect if invalid
                culture = CultureInfo.CurrentUICulture;
            }
        }
        else
        {
            // Autodetect from OS
            culture = CultureInfo.CurrentUICulture;
        }

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public string GetString(string name)
    {
        return _localizer[name];
    }
    
    public string GetString(string name, params object[] arguments)
    {
        return _localizer[name, arguments];
    }
}
