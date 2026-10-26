namespace MicTap.Core.Tests;

public sealed class SessionFoldersTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 10, 26, 14, 5, 59, TimeSpan.FromHours(3));

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Session_is_named_after_its_start_second()
    {
        SessionInfo session = SessionFolders.Create(_temp.Path, Started);

        Assert.Equal("2026-10-26_14-05-59", session.Name);
        Assert.Equal(Path.Combine(_temp.Path, "2026-10-26_14-05-59"), session.Folder);
        Assert.True(Directory.Exists(Path.Combine(session.Folder, "audio")));
    }

    [Fact]
    public void Sessions_in_the_same_second_get_suffixes()
    {
        string[] names = [.. Enumerable.Range(0, 3).Select(_ => SessionFolders.Create(_temp.Path, Started).Name)];

        Assert.Equal(["2026-10-26_14-05-59", "2026-10-26_14-05-59_2", "2026-10-26_14-05-59_3"], names);
    }

    [Fact]
    public void Latest_session_is_found_by_time_then_suffix()
    {
        string[] folders =
        [
            "2026-10-25_23-59-00", "2026-10-26_09-59-59", "2026-10-26_14-05-00", "2026-10-26_14-05-00_2",
            "2026-10-26_14-05-00_10", "notes", "2026-13-01_00-00-00", "2026-10-26_14-05-00_x", "2026-10-26_14-05",
        ];
        foreach (string folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(_temp.Path, folder));
        }

        Assert.Equal("2026-10-26_14-05-00_10", SessionFolders.FindLatest(_temp.Path)?.Name);
    }

    [Theory]
    [InlineData("2026-10-26_1404", "2026-10-26_14-04-30", "2026-10-26_14-04-30")]
    [InlineData("2026-10-26_1406", "2026-10-26_14-05-59", "2026-10-26_1406")]
    public void Sessions_named_by_minute_before_v0_2_1_are_ordered_with_the_new_ones(string old, string current, string latest)
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, old));
        Directory.CreateDirectory(Path.Combine(_temp.Path, current));

        Assert.Equal(latest, SessionFolders.FindLatest(_temp.Path)?.Name);
    }

    [Fact]
    public void Missing_root_has_no_latest_session()
    {
        Assert.Null(SessionFolders.FindLatest(Path.Combine(_temp.Path, "absent")));
    }
}
