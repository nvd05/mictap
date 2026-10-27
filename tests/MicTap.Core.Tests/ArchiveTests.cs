namespace MicTap.Core.Tests;

public sealed class ArchiveTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 22, 0, 0, TimeSpan.FromHours(5));

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Archived_session_leaves_its_project_whole_and_comes_back_with_its_text()
    {
        ProjectInfo project = Projects.Create(_temp.Path, new ProjectSettings("Японский", [ "ja" ], AudioRetention.Keep));
        SessionInfo session = Session(project, "Пара 1");
        new JsonlTranscriptSink(session.Folder).Append(0, 1, "ja", "今日は");

        ArchiveEntry entry = Archive.ArchiveSession(_temp.Path, project, session, Now);

        Assert.Empty(SessionFolders.List(project.Folder));
        Assert.Equal(
            (ArchivedKind.Session, "Пара 1", "Японский", Now),
            (entry.Info.Kind, entry.Info.Name, entry.Info.ProjectName, entry.Info.DeletedAt));
        Assert.Single(Archive.List(_temp.Path));
        Assert.DoesNotContain(Projects.List(_temp.Path), p => p.Folder.Contains(Archive.FolderName));

        string restored = Archive.Restore(_temp.Path, entry);

        Assert.Equal(session.Folder, restored);
        Assert.Equal("今日は", Assert.Single(new JsonlTranscriptSink(restored).Lines).Text);
        Assert.False(File.Exists(Path.Combine(restored, Archive.InfoFile)));
        Assert.Empty(Archive.List(_temp.Path));
    }

    [Fact]
    public void Archived_project_comes_back_with_its_settings_and_sessions_under_a_free_name()
    {
        ProjectInfo project = Projects.Create(_temp.Path, new ProjectSettings("Лекции", [ "ru" ], AudioRetention.None, 2));
        Session(project, "Пара 1");

        ArchiveEntry entry = Archive.ArchiveProject(_temp.Path, project, Now);
        Projects.Create(_temp.Path, new ProjectSettings("Лекции", [ "ru" ], AudioRetention.Keep));
        ProjectInfo restored = Projects.Load(Archive.Restore(_temp.Path, entry));

        Assert.Equal(Path.Combine(_temp.Path, "Лекции_2"), restored.Folder);
        Assert.Equal((AudioRetention.None, 2.0), (restored.Settings.AudioRetention, restored.Settings.PhrasePauseSeconds));
        Assert.Equal("Пара 1", Assert.Single(SessionFolders.List(restored.Folder)).Name);
    }

    [Fact]
    public void Session_whose_project_is_gone_gets_its_project_made_again()
    {
        ProjectInfo project = Projects.Create(_temp.Path, new ProjectSettings("Физика", [ "ru" ], AudioRetention.Keep));
        ArchiveEntry session = Archive.ArchiveSession(_temp.Path, project, Session(project, "Пара 1"), Now);
        Archive.DeleteForever(Archive.ArchiveProject(_temp.Path, project, Now.AddMinutes(1)));

        string restored = Archive.Restore(_temp.Path, session);

        Assert.Equal("Физика", Projects.Load(Path.GetDirectoryName(restored)!).Name);
        Assert.True(File.Exists(Path.Combine(restored, "meta.json")));
    }

    [Fact]
    public void Purge_deletes_for_good_only_what_is_older_than_kept_and_keeping_forever_deletes_nothing()
    {
        ProjectInfo project = Projects.Create(_temp.Path, new ProjectSettings("Общий", [ "ru" ], AudioRetention.Keep));
        ArchiveEntry old = Archive.ArchiveSession(_temp.Path, project, Session(project, "старая"), Now.AddDays(-31));
        ArchiveEntry fresh = Archive.ArchiveSession(_temp.Path, project, Session(project, "свежая"), Now.AddDays(-29));

        IReadOnlyList<ArchiveEntry> forever = Archive.Purge(_temp.Path, null, Now);
        IReadOnlyList<ArchiveEntry> purged = Archive.Purge(_temp.Path, TimeSpan.FromDays(30), Now);

        Assert.Empty(forever);
        Assert.Equal([ old.Folder ], purged.Select(e => e.Folder));
        Assert.False(Directory.Exists(old.Folder));
        Assert.Equal([ fresh.Folder ], Archive.List(_temp.Path).Select(e => e.Folder));
    }

    private static SessionInfo Session(ProjectInfo project, string name)
    {
        SessionInfo session = SessionFolders.Open(project.Folder, name);
        new SessionMeta(
            [ new SessionStart(DateTimeOffset.Now, 0) ], "Mic", MetaFormat.From(PcmFormat.Mono16k), 60, "test",
            [ new LanguageMark(0, "ru") ]).WriteTo(session);
        return session;
    }
}
