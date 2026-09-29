using System.Globalization;
using System.Windows;

namespace Rafferty.UI;

internal static class Localization
{
    public const string DefaultLanguage = "ru-RU";
    public const string EnglishLanguage = "en-US";

    public static string Normalize(string? language) => language switch
    {
        EnglishLanguage => EnglishLanguage,
        DefaultLanguage => DefaultLanguage,
        _ => DefaultLanguage
    };

    public static void Apply(string? language)
    {
        var normalized = Normalize(language);
        var resources = System.Windows.Application.Current.Resources.MergedDictionaries;
        var current = resources.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("Resources/Strings.", StringComparison.OrdinalIgnoreCase) == true);
        if (current is not null) resources.Remove(current);

        resources.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{normalized}.xaml", UriKind.Relative)
        });

        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static string T(string key) =>
        System.Windows.Application.Current.TryFindResource(key)?.ToString() ?? key;
}
