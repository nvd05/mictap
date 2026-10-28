using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MicTap.Cli;
using MicTap.Core;

namespace MicTap.Hub.Tests;

public sealed class WaitTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private HubFixture _hub = null!;

    public async Task InitializeAsync()
    {
        _hub = await HubFixture.StartAsync();
        _hub.State.RecordingStarted(_hub.Session, "ja");
    }

    public async Task DisposeAsync() => await _hub.DisposeAsync();

    [Fact]
    public async Task Unread_segments_come_at_once_and_the_next_call_starts_after_the_cursor()
    {
        _hub.Say("今日は");
        _hub.Say("いい天気ですね");

        CliResult first = await _hub.RunAsync("wait");
        Task<CliResult> second = _hub.RunAsync("wait");
        await Task.Delay(200);
        _hub.Say("そうですね");
        CliResult next = await second.WaitAsync(Patience);

        Assert.Equal(MicTapCli.Ok, first.Code);
        Assert.Equal("first_event", first.Reason);
        Assert.Equal([ 1, 2 ], first.Seqs);
        Assert.Equal("Японский", first.Json.GetProperty("project").GetString());
        Assert.Equal("Пара 1", first.Json.GetProperty("session").GetString());
        Assert.Equal(1, first.Json.GetProperty("from_seq").GetInt32());
        Assert.Equal(2, first.Json.GetProperty("to_seq").GetInt32());
        Assert.Equal("ja", first.Json.GetProperty("lang_now").GetString());
        Assert.Equal([ 3 ], next.Seqs);
        Assert.Equal(3, next.Json.GetProperty("from_seq").GetInt32());
    }

    [Fact]
    public async Task Segments_say_on_the_clock_when_they_were_spoken_with_the_time_zone()
    {
        _hub.Say("今日は");

        CliResult result = await _hub.RunAsync("wait");

        string started = result.Json.GetProperty("session_started").GetString()!;
        JsonElement segment = result.Json.GetProperty("segments")[ 0 ];
        Assert.Matches(@"[+-]\d\d:\d\d$", started);
        Assert.Equal(
            DateTimeOffset.Parse(started) + TimeSpan.FromSeconds(segment.GetProperty("start").GetDouble()),
            segment.GetProperty("start_at").GetDateTimeOffset());
        Assert.Equal(
            DateTimeOffset.Parse(started) + TimeSpan.FromSeconds(segment.GetProperty("end").GetDouble()),
            segment.GetProperty("end_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task Without_flags_it_holds_until_the_first_new_segment()
    {
        Task<CliResult> waiting = _hub.RunAsync("wait", "--agent", "codex");
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        _hub.Say("はじめまして");
        CliResult result = await waiting.WaitAsync(Patience);

        Assert.Equal("first_event", result.Reason);
        Assert.Equal([ 1 ], result.Seqs);
        Assert.Equal("はじめまして", result.Json.GetProperty("segments")[ 0 ].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Min_words_holds_until_the_unread_text_has_enough_words()
    {
        Task<CliResult> waiting = _hub.RunAsync("wait", "--min-words", "5");
        _hub.Say("два слова", "ru");
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        _hub.Say("ещё целых четыре слова", "ru");
        CliResult result = await waiting.WaitAsync(Patience);

        Assert.Equal("min_words", result.Reason);
        Assert.Equal([ 1, 2 ], result.Seqs);
    }

    [Fact]
    public async Task Max_wait_returns_what_has_accumulated()
    {
        _hub.Say("一つ");
        var waited = Stopwatch.StartNew();

        CliResult result = await _hub.RunAsync("wait", "--max-wait", "0.6");

        Assert.Equal("max_wait", result.Reason);
        Assert.Equal([ 1 ], result.Seqs);
        Assert.InRange(waited.Elapsed, TimeSpan.FromSeconds(0.5), Patience);
    }

    [Fact]
    public async Task Both_flags_return_at_whichever_comes_first()
    {
        Task<CliResult> waiting = _hub.RunAsync("wait", "--min-words", "2", "--max-wait", "30");
        _hub.Say("раз два три", "ru");

        CliResult result = await waiting.WaitAsync(Patience);

        Assert.Equal("min_words", result.Reason);
    }

    [Fact]
    public async Task From_start_reads_the_session_again()
    {
        _hub.Say("一");
        _hub.Say("二");
        await _hub.RunAsync("wait");

        CliResult again = await _hub.RunAsync("wait", "--from-start");

        Assert.Equal([ 1, 2 ], again.Seqs);
        Assert.Equal(1, again.Json.GetProperty("from_seq").GetInt32());
    }

    [Fact]
    public async Task Stopping_the_recording_ends_the_wait()
    {
        Task<CliResult> waiting = _hub.RunAsync("wait");
        await Task.Delay(300);

        _hub.State.RecordingStopped(_hub.Session);
        CliResult result = await waiting.WaitAsync(Patience);

        Assert.Equal("session_stopped", result.Reason);
        Assert.Empty(result.Seqs);
        Assert.Equal(0, result.Json.GetProperty("to_seq").GetInt32());
    }

    [Fact]
    public async Task Session_stopped_waits_until_the_last_phrases_are_transcribed()
    {
        Task<CliResult> waiting = _hub.RunAsync("wait", "--min-words", "100");
        await Task.Delay(300);

        _hub.State.RecordingStopped(_hub.Session, transcribing: true);
        _hub.Say("последняя фраза", "ru");
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        _hub.State.Transcribed(_hub.Session);
        CliResult result = await waiting.WaitAsync(Patience);

        Assert.Equal("session_stopped", result.Reason);
        Assert.Equal([ 1 ], result.Seqs);
    }

    [Fact]
    public async Task Transcript_finished_before_the_stop_is_reported_holds_nothing()
    {
        _hub.State.Transcribed(_hub.Session);
        _hub.State.RecordingStopped(_hub.Session, transcribing: true);

        CliResult result = await _hub.RunAsync("wait", "--project", "Японский", "--session", "Пара 1", "--min-words", "100").WaitAsync(Patience);

        Assert.Equal("session_stopped", result.Reason);
    }

    [Fact]
    public async Task Wait_without_a_session_follows_a_stopped_recording_until_its_last_phrases_are_in()
    {
        _hub.State.RecordingStopped(_hub.Session, transcribing: true);
        _hub.Say("хвост", "ru");

        CliResult status = await _hub.RunAsync("status");
        CliResult result = await _hub.RunAsync("wait");
        _hub.State.Transcribed(_hub.Session);
        CliResult after = await _hub.RunAsync("wait");

        Assert.Equal(JsonValueKind.Null, status.Json.GetProperty("recording").ValueKind);
        Assert.Equal(("first_event", "Пара 1"), (result.Reason, result.Json.GetProperty("session").GetString()));
        Assert.Equal([ 1 ], result.Seqs);
        Assert.Equal(MicTapCli.NotFound, after.Code);
    }

    [Fact]
    public async Task Failed_recognition_does_not_hold_session_stopped()
    {
        Task<CliResult> waiting = _hub.RunAsync("wait", "--min-words", "100");
        await Task.Delay(300);

        _hub.State.RecordingStopped(_hub.Session, transcribing: true);
        _hub.State.TranscriptionFailed();

        Assert.Equal("session_stopped", (await waiting.WaitAsync(Patience)).Reason);
    }

    [Fact]
    public async Task Named_session_is_waited_on_without_a_running_recording()
    {
        _hub.State.RecordingStopped(_hub.Session);
        _hub.Say("録音の後");

        CliResult result = await _hub.RunAsync("wait", "--project", "Японский", "--session", "Пара 1");

        Assert.Equal("first_event", result.Reason);
        Assert.Equal([ 1 ], result.Seqs);
    }

    [Fact]
    public async Task Project_alone_means_its_running_recording()
    {
        _hub.Say("一");

        CliResult result = await _hub.RunAsync("wait", "--project", "японский");
        _hub.State.RecordingStopped(_hub.Session);
        CliResult notRecording = await _hub.RunAsync("wait", "--project", "Японский");

        Assert.Equal(("first_event", "Пара 1"), (result.Reason, result.Json.GetProperty("session").GetString()));
        Assert.Equal(MicTapCli.NotFound, notRecording.Code);
        Assert.Contains("not recording", notRecording.Error);
    }

    [Fact]
    public async Task Newer_wait_of_the_same_agent_takes_over_and_the_older_leaves_the_cursor_alone()
    {
        _hub.Say("一");
        Task<CliResult> older = _hub.RunAsync("wait", "--min-words", "100", "--max-wait", "30");
        await Task.Delay(300);

        CliResult newer = await _hub.RunAsync("wait", "--min-words", "100", "--max-wait", "0.5");
        CliResult taken = await older.WaitAsync(Patience);
        _hub.Say("二");
        CliResult next = await _hub.RunAsync("wait");

        Assert.Equal(MicTapCli.Superseded, taken.Code);
        Assert.Contains("took over", taken.Error);
        Assert.Equal("max_wait", newer.Reason);
        Assert.Equal([ 1 ], newer.Seqs);
        Assert.Equal([ 2 ], next.Seqs);
    }

    [Fact]
    public async Task Waits_of_different_agents_do_not_take_over_each_other()
    {
        Task<CliResult> claude = _hub.RunAsync("wait", "--agent", "claude");
        Task<CliResult> codex = _hub.RunAsync("wait", "--agent", "codex");
        await Task.Delay(300);
        Assert.False(claude.IsCompleted);

        _hub.Say("一");
        CliResult[] results = await Task.WhenAll(claude, codex).WaitAsync(Patience);

        Assert.All(results, result => Assert.Equal([ 1 ], result.Seqs));
    }

    [Fact]
    public async Task Wait_whose_caller_went_away_leaves_the_cursor_alone()
    {
        _hub.Say("一");
        using var gone = new CancellationTokenSource();
        Task<CliResult> abandoned = _hub.RunAsync(gone.Token, "wait", "--min-words", "100", "--max-wait", "1");
        await Task.Delay(300);

        await gone.CancelAsync();
        Assert.NotEqual(MicTapCli.Ok, (await abandoned.WaitAsync(Patience)).Code);
        await Task.Delay(1500); // past its --max-wait: had it gone on, it would have taken seq 1

        CliResult next = await _hub.RunAsync("wait");

        Assert.Equal([ 1 ], next.Seqs);
        Assert.Equal(AgentState.Thinking, Assert.Single(_hub.State.AgentsOf(_hub.Session)).State);
    }

    [Fact]
    public async Task Push_ends_every_wait_on_the_session_at_once_below_min_words()
    {
        _hub.Say("одно слово", "ru");
        Task<CliResult> claude = _hub.RunAsync("wait", "--agent", "claude", "--min-words", "100");
        Task<CliResult> codex = _hub.RunAsync("wait", "--agent", "codex", "--min-words", "100", "--max-wait", "30");
        await Task.Delay(300);

        _hub.State.Push(_hub.Session);

        foreach (CliResult result in await Task.WhenAll(claude, codex).WaitAsync(Patience))
        {
            Assert.Equal("force_push", result.Reason);
            Assert.Equal([ 1 ], result.Seqs);
        }
    }

    [Fact]
    public async Task Push_with_nobody_waiting_makes_the_next_wait_return_at_once_and_only_that_one()
    {
        _hub.State.Push(_hub.Session);

        CliResult pushed = await _hub.RunAsync("wait", "--min-words", "100").WaitAsync(Patience);
        CliResult after = await _hub.RunAsync("wait", "--max-wait", "0.3");

        Assert.Equal("force_push", pushed.Reason);
        Assert.Empty(pushed.Seqs);
        Assert.Equal("max_wait", after.Reason);
    }

    [Fact]
    public async Task Push_does_not_outlive_its_recording()
    {
        _hub.State.Push(_hub.Session);
        _hub.State.RecordingStopped(_hub.Session);
        _hub.State.Push(_hub.Session);
        _hub.State.RecordingStarted(_hub.Session, "ja");

        CliResult result = await _hub.RunAsync("wait", "--max-wait", "0.3");

        Assert.Equal("max_wait", result.Reason);
    }

    [Fact]
    public async Task Cursor_and_presence_of_the_agent_are_kept()
    {
        _hub.Say("一");
        Task<CliResult> waiting = _hub.RunAsync("wait", "--agent", "claude", "--min-words", "100", "--max-wait", "1");
        await Task.Delay(300);
        Assert.Equal(AgentState.Waiting, Assert.Single(_hub.State.AgentsOf(_hub.Session)).State);

        await waiting.WaitAsync(Patience);
        await Task.Delay(300);

        AgentPresence agent = Assert.Single(_hub.State.AgentsOf(_hub.Session));
        Assert.Equal(("claude", AgentState.Thinking), (agent.Name, agent.State));
        AgentCursor cursor = AgentCursors.Read(_hub.Session, "claude")!;
        Assert.Equal(1, cursor.Cursor);
        Assert.Equal(new AgentPolicy(100, 1), cursor.Policy);
    }

    [Fact]
    public async Task Only_an_open_wait_counts_as_waiting_not_the_status_call_itself()
    {
        _hub.Say("一");
        await _hub.RunAsync("wait");

        CliResult status = await _hub.RunAsync("status");

        JsonElement agent = Assert.Single(status.Json.GetProperty("agents").EnumerateArray());
        Assert.Equal(("claude", "thinking"), (agent.GetProperty("name").GetString(), agent.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task Status_projects_sessions_and_tail_print_json()
    {
        _hub.Say("一");
        _hub.Say("二");

        CliResult status = await _hub.RunAsync("status");
        CliResult projects = await _hub.RunAsync("projects");
        CliResult sessions = await _hub.RunAsync("sessions", "--project", "Японский");
        CliResult tail = await _hub.RunAsync("tail", "--session", "Пара 1", "--seq", "2");

        Assert.Equal("Пара 1", status.Json.GetProperty("recording").GetProperty("session").GetString());
        Assert.Equal("ja", status.Json.GetProperty("recording").GetProperty("lang_now").GetString());
        Assert.Equal("Японский", projects.Json[ 0 ].GetProperty("name").GetString());
        Assert.Equal(1, projects.Json[ 0 ].GetProperty("sessions").GetInt32());
        Assert.Equal(2, sessions.Json[ 0 ].GetProperty("lines").GetInt32());
        Assert.Equal([ 2 ], tail.Json.GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("seq").GetInt32()));
        Assert.Equal("Пара 1", tail.Json.GetProperty("session").GetString());
    }

    [Fact]
    public async Task Unknown_project_or_session_and_nothing_recording_exit_with_2()
    {
        CliResult unknownSession = await _hub.RunAsync("wait", "--project", "Японский", "--session", "нет такой");
        CliResult unknownProject = await _hub.RunAsync("sessions", "--project", "Физика");
        _hub.State.RecordingStopped(_hub.Session);
        CliResult nothingRecording = await _hub.RunAsync("wait");

        Assert.Equal(MicTapCli.NotFound, unknownSession.Code);
        Assert.Contains("нет такой", unknownSession.Error);
        Assert.Equal(MicTapCli.NotFound, unknownProject.Code);
        Assert.Equal(MicTapCli.NotFound, nothingRecording.Code);
        Assert.Contains("no recording", nothingRecording.Error);
    }

    [Theory]
    [InlineData("wait", "--min-words", "0")]
    [InlineData("wait", "--max-wait", "-1")]
    [InlineData("wait", "--bogus")]
    [InlineData("sessions")]
    [InlineData("dance")]
    public async Task Wrong_arguments_exit_with_4(params string[] args)
    {
        CliResult result = await _hub.RunAsync(args);

        Assert.Equal(MicTapCli.BadArguments, result.Code);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public async Task No_hub_exits_with_3()
    {
        int port = FreePort();
        var error = new StringWriter();

        int code = await MicTapCli.RunAsync([ "status" ], port, new StringWriter(), error, CancellationToken.None);

        Assert.Equal(MicTapCli.HubDown, code);
        Assert.Contains("not running", error.ToString());
    }

    [Fact]
    public async Task Wait_help_explains_the_loop()
    {
        CliResult help = await _hub.RunAsync("wait", "--help");

        Assert.Equal(MicTapCli.Ok, help.Code);
        Assert.Contains("call it again", help.Output);
        Assert.Contains("--min-words", help.Output);
        Assert.Contains("force_push", help.Output);
        Assert.Contains("45 or less", help.Output);
    }

    [Fact]
    public async Task Taken_port_is_reported_as_an_io_error()
    {
        await Assert.ThrowsAnyAsync<IOException>(() => HubHost.StartAsync(_hub.Host.Port, _hub.State));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

public sealed class WordCountTests
{
    [Theory]
    [InlineData("раз два три", 3)]
    [InlineData("  Ну, — так  ", 2)]
    [InlineData("今日はいい天気", 4)]
    [InlineData("Это 日本語 test", 4)]
    [InlineData("", 0)]
    public void Words_are_tokens_and_cjk_counts_two_characters_a_word(string text, int words)
    {
        Assert.Equal(words, WordCount.Of(text));
    }
}
