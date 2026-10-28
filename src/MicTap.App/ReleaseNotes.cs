using System.IO;

namespace MicTap.App;

/// <summary>A part of a release: «Добавлено», «Изменено», «Исправлено» or «Удалено», and its lines.</summary>
public sealed record ReleaseSection(string Title, IReadOnlyList<string> Items)
{
    /// <summary>A PackIcon kind for the title, as GitHub releases mark their parts.</summary>
    public string Icon => Title switch
    {
        "Добавлено" => "PlusCircleOutline",
        "Исправлено" => "WrenchOutline",
        "Удалено" => "MinusCircleOutline",
        _ => "PencilCircleOutline",
    };
}

/// <summary>A version in «Что нового».</summary>
public sealed record Release(string Version, string Date, IReadOnlyList<ReleaseSection> Sections)
{
    public override string ToString() => Version;
}

/// <summary>
/// CHANGELOG.md of the repository, built into the app: «## 0.11.0 — 2026-10-28» starts a release, «### Добавлено» a
/// part of it, «- …» a line. Only this much of Markdown is used there, so only this much is read.
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
        List<string> items = [];

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
                items.Add(line[ 2.. ].Trim());
            }
        }

        EndRelease();
        return releases;
    }
}
