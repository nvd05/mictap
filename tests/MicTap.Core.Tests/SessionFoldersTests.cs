namespace MicTap.Core.Tests;

public sealed class SessionFoldersTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 10, 26, 14, 5, 59, TimeSpan.FromHours(3));

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Session_is_named_after_its_start_minute()
    {
        SessionInfo session = SessionFolders.Create(_temp.Path, Started);

        Assert.Equal("2026-10-26_1405", session.Name);
        Assert.Equal(Path.Combine(_temp.Path, "2026-10-26_1405"), session.Folder);
        Assert.True(Directory.Exists(Path.Combine(session.Folder, "audio")));
    }

    [Fact]
    public void Sessions_in_the_same_minute_get_suffixes()
    {
        string[] names = [.. Enumerable.Range(0, 3).Select(_ => SessionFolders.Create(_temp.Path, Started).Name)];

        Assert.Equal(["2026-10-26_1405", "2026-10-26_1405_2", "2026-10-26_1405_3"], names);
    }

    [Fact]
    public void Latest_session_is_found_by_time_then_suffix()
    {
        string[] folders =
        [
            "2026-10-25_2359", "2026-10-26_0959", "2026-10-26_1405", "2026-10-26_1405_2", "2026-10-26_1405_10",
            "notes", "2026-13-01_0000", "2026-10-26_1405_x",
        ];
        foreach (string folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(_temp.Path, folder));
        }

        Assert.Equal("2026-10-26_1405_10", SessionFolders.FindLatest(_temp.Path)?.Name);
    }

    [Fact]
    public void Missing_root_has_no_latest_session()
    {
        Assert.Null(SessionFolders.FindLatest(Path.Combine(_temp.Path, "absent")));
    }
}
