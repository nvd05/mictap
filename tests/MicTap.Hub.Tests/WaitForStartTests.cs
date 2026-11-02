using System.Text.Json;
using MicTap.Cli;
using MicTap.Core;

namespace MicTap.Hub.Tests;

public sealed class WaitForStartTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Wait_for_start_holds_until_a_recording_begins()
    {
        await using HubFixture hub = await HubFixture.StartAsync();

        Task<CliResult> waiting = hub.RunAsync("wait", "--wait-for-start");
        await Task.Delay(250);
        Assert.False(waiting.IsCompleted);

        hub.State.RecordingStarted(hub.Session, "ja");
        hub.Say("はじめまして");
        CliResult result = await waiting.WaitAsync(Patience);

        Assert.Equal("first_event", result.Reason);
        Assert.Equal([ 1 ], result.Seqs);
        Assert.Equal("Пара 1", result.Json.GetProperty("session").GetString());
    }

    [Fact]
    public async Task After_session_stopped_the_agent_waits_for_the_next_recording_of_the_same_session()
    {
        await using HubFixture hub = await HubFixture.StartAsync();
        hub.State.RecordingStarted(hub.Session, "ja");
        hub.Say("一");
        await hub.RunAsync("wait");
        hub.State.RecordingStopped(hub.Session);

        Task<CliResult> waiting = hub.RunAsync("wait", "--project", "Японский", "--session", "Пара 1", "--wait-for-start");
        await Task.Delay(250);
        Assert.False(waiting.IsCompleted);

        hub.State.RecordingStarted(hub.Session, "ja");
        hub.Say("二");
        CliResult result = await waiting.WaitAsync(Patience);

        Assert.Equal(("first_event", 2), (result.Reason, Assert.Single(result.Seqs)));
        Assert.Equal(2, result.Json.GetProperty("from_seq").GetInt32());
    }

    [Fact]
    public async Task Project_wait_for_start_ignores_another_projects_recording()
    {
        await using HubFixture hub = await HubFixture.StartAsync();
        ProjectInfo other = Projects.Create(Path.GetDirectoryName(hub.Project.Folder)!, new ProjectSettings("Физика", [ "ru" ], AudioRetention.Keep));
        SessionInfo otherSession = SessionFolders.Open(other.Folder, "Лекция");
        new SessionMeta(
            [ new SessionStart(DateTimeOffset.Now, 0) ], "Mic", MetaFormat.From(PcmFormat.Mono16k), 60, "test",
            [ new LanguageMark(0, "ru") ]).WriteTo(otherSession);

        Task<CliResult> waiting = hub.RunAsync("wait", "--project", "Японский", "--wait-for-start");
        hub.State.RecordingStarted(otherSession, "ru");
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        hub.State.RecordingStopped(otherSession);
        hub.State.RecordingStarted(hub.Session, "ja");
        hub.Say("日本語");
        CliResult result = await waiting.WaitAsync(Patience);

        Assert.Equal(("Японский", "Пара 1"), (result.Json.GetProperty("project").GetString(), result.Json.GetProperty("session").GetString()));
        Assert.Equal([ 1 ], result.Seqs);
    }

    [Fact]
    public async Task Manual_text_and_force_push_work_without_a_recording()
    {
        await using HubFixture hub = await HubFixture.StartAsync();
        hub.State.RecordingStopped(hub.Session);
        hub.Say("текст, набранный руками", "ru");
        hub.State.PushText(hub.Session);
        CliResult result = await hub.RunAsync("wait", "--project", "Японский", "--session", "Пара 1", "--min-words", "100")
            .WaitAsync(Patience);

        Assert.Equal("force_push", result.Reason);
        Assert.Equal("текст, набранный руками", result.Json.GetProperty("segments")[ 0 ].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Wait_for_start_does_not_replay_text_typed_before_the_recording()
    {
        await using HubFixture hub = await HubFixture.StartAsync();
        SessionInfo text = SessionFolders.OpenText(hub.Project.Folder, "Заметки", "ru", "test");
        new JsonlTranscriptSink(text.Folder).Append(0, 0, "ru", "старый текст");
        hub.State.RecordingStarted(text, "ru");
        var sink = new JsonlTranscriptSink(text.Folder);
        sink.Append(0, 0, "ru", "сказано после старта");
        hub.State.Pulse(text.Folder);

        CliResult result = await hub.RunAsync(
            "wait", "--project", "Японский", "--session", "Заметки", "--wait-for-start");

        Assert.Equal("first_event", result.Reason);
        Assert.Equal(2, Assert.Single(result.Seqs));
    }

    [Fact]
    public async Task A_rename_promised_during_recording_holds_session_stopped_until_the_folder_moves()
    {
        await using HubFixture hub = await HubFixture.StartAsync();
        hub.State.RecordingStarted(hub.Session, "ja");
        Task<CliResult> waiting = hub.RunAsync("wait", "--min-words", "100");
        await Task.Delay(250);

        hub.State.ProjectRenamePending(hub.Project.Folder);
        hub.State.RecordingStopped(hub.Session);
        await Task.Delay(400);
        Assert.False(waiting.IsCompleted);

        ProjectInfo before = hub.Project;
        ProjectInfo after = Projects.Rename(before, "Новый");
        hub.State.ProjectRenamed(before.Folder, after.Folder, before.Name, after.Name);
        CliResult moved = await waiting.WaitAsync(Patience);

        Assert.Equal("project_moved", moved.Reason);
        Assert.Equal("Новый", moved.Json.GetProperty("project").GetString());
    }

    [Fact]
    public async Task Project_rename_ends_the_wait_with_the_new_name_and_path_and_keeps_the_cursor()
    {
        await using HubFixture hub = await HubFixture.StartAsync();
        hub.State.RecordingStarted(hub.Session, "ja");
        hub.Say("一");

        Task<CliResult> waiting = hub.RunAsync("wait", "--min-words", "100");
        await Task.Delay(250);
        Assert.False(waiting.IsCompleted);

        ProjectInfo before = hub.Project;
        ProjectInfo after = Projects.Rename(before, "Новый");
        hub.State.ProjectRenamed(before.Folder, after.Folder, before.Name, after.Name);
        CliResult moved = await waiting.WaitAsync(Patience);

        Assert.Equal("project_moved", moved.Reason);
        Assert.Empty(moved.Seqs);
        Assert.Equal(0, moved.Json.GetProperty("to_seq").GetInt32());
        JsonElement move = moved.Json.GetProperty("move");
        Assert.Equal(("Японский", "Новый"), (move.GetProperty("old_name").GetString(), move.GetProperty("new_name").GetString()));
        Assert.Equal(Path.GetFullPath(before.Folder), Path.GetFullPath(move.GetProperty("old_path").GetString()!));
        Assert.Equal(Path.GetFullPath(after.Folder), Path.GetFullPath(move.GetProperty("new_path").GetString()!));

        CliResult next = await hub.RunAsync("wait", "--project", "Новый", "--session", "Пара 1");
        Assert.Equal("first_event", next.Reason);
        Assert.Equal([ 1 ], next.Seqs);
    }
}
