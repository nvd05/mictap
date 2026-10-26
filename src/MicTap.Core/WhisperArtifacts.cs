using System.Text.RegularExpressions;

namespace MicTap.Core;

/// <summary>
/// Phrases Whisper makes up from noise and near-silence, learned from video subtitles: «Продолжение следует…»,
/// subtitle credits, «Спасибо за просмотр». A segment whose whole text is one of them is not speech: it is left out
/// of transcript.jsonl and only logged. Text that merely contains such a phrase is kept.
/// </summary>
public static partial class WhisperArtifacts
{
    // Whole texts after Normalize: lower case, ё as е, punctuation and symbols dropped, spaces collapsed.
    private static readonly Regex[] Known =
    [
        Whole("продолжение следует"),
        Whole("спасибо за просмотр( и до (новых|скорых) встреч| до новых встреч)?"),
        Whole("(ставьте лайки и )?подписывайтесь на (мой |наш )?канал"),
        Whole("(редактор|корректор) субтитров .+"),
        Whole("субтитры (сделал|сделала|создавал|создал|подготовил|подогнал|делал) .+"),
        Whole("субтитры (сделаны|созданы|подготовлены) .+"),
        Whole("(最後まで)?ご視聴(いただき)?ありがとうございました"),
        Whole("チャンネル登録(を)?お願いします"),
    ];

    public static bool IsArtifact(string text)
    {
        string normalized = Normalize(text);
        return normalized.Length > 0 && Known.Any(pattern => pattern.IsMatch(normalized));
    }

    private static string Normalize(string text) =>
        Spaces().Replace(PunctuationAndSymbols().Replace(text.ToLowerInvariant().Replace('ё', 'е'), " "), " ").Trim();

    private static Regex Whole(string pattern) => new($"^(?:{pattern})$", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"[\p{P}\p{S}]+")]
    private static partial Regex PunctuationAndSymbols();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
