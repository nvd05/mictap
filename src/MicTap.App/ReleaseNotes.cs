using System.Globalization;
using System.IO;

namespace MicTap.App;

/// <summary>A line of a release: its bold lead («Боковое меню.») and the rest; either may be empty.</summary>
public sealed record ReleaseItem(string Lead, string Text)
{
    /// <summary>«**Lead.** text» → the lead in bold and the text after it; a line without ** is all text.</summary>
    public static ReleaseItem Parse(string line)
    {
        if (line.StartsWith("**", StringComparison.Ordinal) && line.IndexOf("**", 2, StringComparison.Ordinal) is > 2 and int end)
        {
            return new ReleaseItem(line[ 2..end ], line[ (end + 2).. ]);
        }

        return new ReleaseItem("", line);
    }
}

/// <summary>A part of a release: «Добавлено», «Изменено», «Исправлено» or «Удалено», and its lines.</summary>
public sealed record ReleaseSection(string Title, IReadOnlyList<ReleaseItem> Items);

/// <summary>A version in «Что нового»; <paramref name="Date"/> is yyyy-MM-dd as CHANGELOG.md writes it.</summary>
public sealed record Release(string Version, string Date, IReadOnlyList<ReleaseSection> Sections)
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public string Title => $"Версия {Version}";

    /// <summary>«28 октября 2026».</summary>
    public string DateText => DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
        ? day.ToString("d MMMM yyyy", Russian)
        : Date;

    public override string ToString() => Title;
}

/// <summary>
/// CHANGELOG.md of the repository, built into the app: «## 0.11.0 — 2026-10-28» starts a release, «### Добавлено» a
/// part of it, «- **Lead.** text» a line. Only this much of Markdown is used there, so only this much is read.
/// </summary>
public static class ReleaseNotes
{
    public const string ResourceName = "MicTap.App.CHANGELOG.md";

    public static IReadOnlyList<Release> Load()
    {
        using Stream stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not built into the app.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<Release> Parse(string markdown)
    {
        List<Release> releases = [];
        string? version = null;
        string date = "";
        List<ReleaseSection> sections = [];
        string? section = null;
        List<ReleaseItem> items = [];

        void EndSection()
        {
            if (section is not null)
            {
                sections.Add(new ReleaseSection(section, [ .. items ]));
            }

            section = null;
            items.Clear();
        }

        void EndRelease()
        {
            EndSection();
            if (version is not null)
            {
                releases.Add(new Release(version, date, [ .. sections ]));
            }

            version = null;
            sections.Clear();
        }

        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                EndRelease();
                string[] parts = line[ 3.. ].Split(" — ", 2, StringSplitOptions.TrimEntries);
                version = parts[ 0 ];
                date = parts.Length > 1 ? parts[ 1 ] : "";
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                EndSection();
                section = line[ 4.. ].Trim();
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) && section is not null)
            {
                items.Add(ReleaseItem.Parse(line[ 2.. ].Trim()));
            }
        }

        EndRelease();
        return releases;
    }
}
