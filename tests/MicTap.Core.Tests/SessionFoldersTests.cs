namespace MicTap.Core.Tests;

public sealed class SessionFoldersTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Default_name_is_the_start_time_to_the_second()
    {
        Assert.Equal("2026-10-26_14-05-59", SessionFolders.DefaultName(new DateTimeOffset(2026, 10, 26, 14, 5, 59, TimeSpan.FromHours(3))));
    }

    [Theory]
    [InlineData("Лекция 1: введение", "Лекция 1 введение")]
    [InlineData("  a/b\\c*d?  ", "abcd")]
    [InlineData("итоги...", "итоги")]
    [InlineData("много   пробелов", "много пробелов")]
    [InlineData("con", "con_")]
    [InlineData("LPT1.txt", "LPT1.txt_")]
    [InlineData("<>:|", "")]
    public void Typed_names_become_safe_folder_names(string typed, string folder)
    {
        Assert.Equal(folder, SessionFolders.Sanitize(typed));
    }

    [Fact]
    public void Open_creates_the_session_folders_and_keeps_an_existing_session()
    {
        SessionInfo session = SessionFolders.Open(_temp.Path, "Пара 1");
        File.WriteAllText(Path.Combine(session.AudioFolder, "000.wav"), "x");

        SessionInfo again = SessionFolders.Open(_temp.Path, "Пара 1");

        Assert.Equal(session, again);
        Assert.True(Directory.Exists(session.AgentsFolder));
        Assert.True(Directory.Exists(session.NotesFolder));
        Assert.True(File.Exists(Path.Combine(session.AudioFolder, "000.wav")));
    }

    [Fact]
    public void Sessions_are_listed_most_recently_recorded_first_and_other_folders_ignored()
    {
        SessionInfo older = SessionFolders.Open(_temp.Path, "старая");
        SessionInfo newer = SessionFolders.Open(_temp.Path, "новая");
        File.WriteAllText(older.MetaFile, "{}");
        File.WriteAllText(newer.MetaFile, "{}");
        File.SetLastWriteTimeUtc(older.MetaFile, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "не сессия"));

        Assert.Equal([ "новая", "старая" ], SessionFolders.List(_temp.Path).Select(s => s.Name));
    }

    [Fact]
    public void Missing_project_has_no_sessions()
    {
        Assert.Empty(SessionFolders.List(Path.Combine(_temp.Path, "absent")));
    }
}
