namespace MicTap.Core;

/// <summary>A language a project may use: its Whisper code and its names for search and display.</summary>
public sealed record LanguageInfo(string Code, string RussianName, string EnglishName)
{
    /// <summary>How a list shows it: «Русский · ru».</summary>
    public override string ToString() => $"{RussianName} · {Code}";
}

/// <summary>
/// Languages mictap offers. Search goes by Russian name, English name or code: «рус», Russian and ru all find
/// Russian. Nothing outside this list is accepted, so a typo never becomes a language of its own.
/// </summary>
public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageInfo> All { get; } =
    [
        new("af", "Африкаанс", "Afrikaans"),
        new("am", "Амхарский", "Amharic"),
        new("ar", "Арабский", "Arabic"),
        new("as", "Ассамский", "Assamese"),
        new("az", "Азербайджанский", "Azerbaijani"),
        new("ba", "Башкирский", "Bashkir"),
        new("be", "Белорусский", "Belarusian"),
        new("bg", "Болгарский", "Bulgarian"),
        new("bn", "Бенгальский", "Bengali"),
        new("bo", "Тибетский", "Tibetan"),
        new("br", "Бретонский", "Breton"),
        new("bs", "Боснийский", "Bosnian"),
        new("ca", "Каталанский", "Catalan"),
        new("cs", "Чешский", "Czech"),
        new("cy", "Валлийский", "Welsh"),
        new("da", "Датский", "Danish"),
        new("de", "Немецкий", "German"),
        new("el", "Греческий", "Greek"),
        new("en", "Английский", "English"),
        new("eo", "Эсперанто", "Esperanto"),
        new("es", "Испанский", "Spanish"),
        new("et", "Эстонский", "Estonian"),
        new("eu", "Баскский", "Basque"),
        new("fa", "Персидский", "Persian"),
        new("fi", "Финский", "Finnish"),
        new("fo", "Фарерский", "Faroese"),
        new("fr", "Французский", "French"),
        new("gl", "Галисийский", "Galician"),
        new("gu", "Гуджарати", "Gujarati"),
        new("ha", "Хауса", "Hausa"),
        new("haw", "Гавайский", "Hawaiian"),
        new("he", "Иврит", "Hebrew"),
        new("hi", "Хинди", "Hindi"),
        new("hr", "Хорватский", "Croatian"),
        new("ht", "Гаитянский креольский", "Haitian Creole"),
        new("hu", "Венгерский", "Hungarian"),
        new("hy", "Армянский", "Armenian"),
        new("id", "Индонезийский", "Indonesian"),
        new("is", "Исландский", "Icelandic"),
        new("it", "Итальянский", "Italian"),
        new("ja", "Японский", "Japanese"),
        new("jw", "Яванский", "Javanese"),
        new("ka", "Грузинский", "Georgian"),
        new("kk", "Казахский", "Kazakh"),
        new("km", "Кхмерский", "Khmer"),
        new("kn", "Каннада", "Kannada"),
        new("ko", "Корейский", "Korean"),
        new("la", "Латинский", "Latin"),
        new("lb", "Люксембургский", "Luxembourgish"),
        new("ln", "Лингала", "Lingala"),
        new("lo", "Лаосский", "Lao"),
        new("lt", "Литовский", "Lithuanian"),
        new("lv", "Латышский", "Latvian"),
        new("mg", "Малагасийский", "Malagasy"),
        new("mi", "Маори", "Maori"),
        new("mk", "Македонский", "Macedonian"),
        new("ml", "Малаялам", "Malayalam"),
        new("mn", "Монгольский", "Mongolian"),
        new("mr", "Маратхи", "Marathi"),
        new("ms", "Малайский", "Malay"),
        new("mt", "Мальтийский", "Maltese"),
        new("my", "Бирманский", "Myanmar"),
        new("ne", "Непальский", "Nepali"),
        new("nl", "Нидерландский", "Dutch"),
        new("nn", "Норвежский нюнорск", "Norwegian Nynorsk"),
        new("no", "Норвежский", "Norwegian"),
        new("oc", "Окситанский", "Occitan"),
        new("pa", "Пенджабский", "Punjabi"),
        new("pl", "Польский", "Polish"),
        new("ps", "Пушту", "Pashto"),
        new("pt", "Португальский", "Portuguese"),
        new("ro", "Румынский", "Romanian"),
        new("ru", "Русский", "Russian"),
        new("sa", "Санскрит", "Sanskrit"),
        new("sd", "Синдхи", "Sindhi"),
        new("si", "Сингальский", "Sinhala"),
        new("sk", "Словацкий", "Slovak"),
        new("sl", "Словенский", "Slovenian"),
        new("sn", "Шона", "Shona"),
        new("so", "Сомалийский", "Somali"),
        new("sq", "Албанский", "Albanian"),
        new("sr", "Сербский", "Serbian"),
        new("su", "Сунданский", "Sundanese"),
        new("sv", "Шведский", "Swedish"),
        new("sw", "Суахили", "Swahili"),
        new("ta", "Тамильский", "Tamil"),
        new("te", "Телугу", "Telugu"),
        new("tg", "Таджикский", "Tajik"),
        new("th", "Тайский", "Thai"),
        new("tk", "Туркменский", "Turkmen"),
        new("tl", "Тагальский", "Tagalog"),
        new("tr", "Турецкий", "Turkish"),
        new("tt", "Татарский", "Tatar"),
        new("uk", "Украинский", "Ukrainian"),
        new("ur", "Урду", "Urdu"),
        new("uz", "Узбекский", "Uzbek"),
        new("vi", "Вьетнамский", "Vietnamese"),
        new("yi", "Идиш", "Yiddish"),
        new("yo", "Йоруба", "Yoruba"),
        new("zh", "Китайский", "Chinese"),
    ];

    private static readonly Dictionary<string, LanguageInfo> ByCode =
        All.ToDictionary(language => language.Code, StringComparer.OrdinalIgnoreCase);

    public static bool Supports(string code) => ByCode.ContainsKey(code);

    public static LanguageInfo? Find(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>
    /// Languages matching <paramref name="query"/> by code, Russian or English name; favorites first, then by name.
    /// An empty query shows all.
    /// </summary>
    public static IReadOnlyList<LanguageInfo> Search(string? query, IReadOnlyList<string>? favorites = null)
    {
        string text = query?.Trim() ?? "";
        HashSet<string> liked = new(favorites ?? [], StringComparer.OrdinalIgnoreCase);
        return
        [
            .. All
                .Where(language => text.Length == 0
                    || language.Code.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || language.RussianName.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || language.EnglishName.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(language => liked.Contains(language.Code))
                .ThenBy(language => language.RussianName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }
}
