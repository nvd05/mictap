using System.Globalization;

namespace MicTap.Core;

public sealed record SupportedLanguage(string Code, string Name, string EnglishName, string NativeName)
{
    public bool Matches(string query) => new[] { Code, Name, EnglishName, NativeName }
        .Any(value => value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Whisper's supported language codes; search never creates an arbitrary language.</summary>
public static class LanguageCatalog
{
    private const string Codes = "en zh de es ru ko fr ja pt tr pl ca nl ar sv it id hi fi vi he uk el ms cs ro da hu ta no th ur hr bg lt la mi ml cy sk te fa lv bn sr az sl kn et mk br eu is hy ne mn bs kk sq sw gl mr pa si km sn yo so af oc ka be tg sd gu am yi lo uz fo ht ps tk nn mt sa lb my bo tl mg as tt haw ln ha ba jw su yue";
    private static readonly Dictionary<string, string> RussianNames = new()
    {
        [ "ru" ] = "Русский",
        [ "ja" ] = "Японский",
        [ "en" ] = "Английский",
        [ "zh" ] = "Китайский",
        [ "de" ] = "Немецкий",
        [ "fr" ] = "Французский",
        [ "es" ] = "Испанский",
        [ "ko" ] = "Корейский",
        [ "it" ] = "Итальянский",
        [ "pt" ] = "Португальский",
        [ "uk" ] = "Украинский",
        [ "ar" ] = "Арабский",
        [ "tr" ] = "Турецкий",
        [ "pl" ] = "Польский",
        [ "hi" ] = "Хинди",
        [ "yue" ] = "Кантонский",
        [ "he" ] = "Иврит",
        [ "jw" ] = "Яванский",
        [ "haw" ] = "Гавайский",
        [ "la" ] = "Латинский",
    };

    public static IReadOnlyList<SupportedLanguage> All { get; } = [ .. Codes.Split(' ').Select(Create) ];
    public static bool Supports(string code) => All.Any(language => language.Code == code);
    public static SupportedLanguage? Find(string code) => All.FirstOrDefault(language => language.Code == code);

    private static SupportedLanguage Create(string code)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(code == "jw" ? "jv" : code);
        string name = RussianNames.GetValueOrDefault(code, culture.DisplayName);
        return new SupportedLanguage(code, name, culture.EnglishName, culture.NativeName);
    }
}
