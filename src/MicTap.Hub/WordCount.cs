namespace MicTap.Hub;

/// <summary>
/// Words in recognized text for --min-words. Text with spaces counts its tokens; Japanese, Chinese and Korean have
/// no spaces, so there every two characters of their scripts count as a word.
/// </summary>
public static class WordCount
{
    public static int Of(string text)
    {
        int words = 0;
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int cjk = token.Count(IsCjk);
            if (cjk > 0)
            {
                words += (cjk + 1) / 2;
            }
            else if (token.Any(char.IsLetterOrDigit))
            {
                words++;
            }
        }

        return words;
    }

    private static bool IsCjk(char c) =>
        c is (>= '぀' and <= 'ヿ') or (>= '一' and <= '鿿') or (>= '가' and <= '힯');
}
