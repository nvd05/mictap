using System.Text.Json;
using MicTap.Cli;
using MicTap.Core;

namespace MicTap.Hub.Tests;

/// <summary>A hub on a free port over a temporary root with one project and one recording session.</summary>
public sealed class HubFixture : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mictap-hub-tests", Guid.NewGuid().ToString("N"));

    private HubFixture()
    {
        Directory.CreateDirectory(_root);
        Project = Projects.Create(_root, new ProjectSettings("Японский", [ "ja", "ru" ], AudioRetention.Keep));
        Session = SessionFolders.Open(Project.Folder, "Пара 1");
        new SessionMeta(
            [ new SessionStart(DateTimeOffset.Now, 0) ], "Mic", MetaFormat.From(PcmFormat.Mono16k), 60, "0.5.0-test",
            [ new LanguageMark(0, "ja") ]).WriteTo(Session);
        Transcript = new JsonlTranscriptSink(Session.Folder);
        State = new AgentsState(_root, s => new JsonlTranscriptSink(s.Folder).Lines, TimeProvider.System, "0.5.0-test");
    }

    public ProjectInfo Project { get; }

    public SessionInfo Session { get; }

    public JsonlTranscriptSink Transcript { get; }

    public AgentsState State { get; }

    public HubHost Host { get; private set; } = null!;

    public static async Task<HubFixture> StartAsync()
    {
        var fixture = new HubFixture();
        fixture.Host = await HubHost.StartAsync(0, fixture.State);
        return fixture;
    }

    /// <summary>The recognizer wrote a segment: append it and wake waiting agents, as the pipeline does.</summary>
    public void Say(string text, string lang = "ja")
    {
        double start = Transcript.Lines.Count;
        Transcript.Append(start, start + 1, lang, text);
        State.Pulse(Session.Folder);
    }

    public async Task<CliResult> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await MicTapCli.RunAsync(args, Host.Port, output, error, CancellationToken.None);
        return new CliResult(code, output.ToString().Trim(), error.ToString().Trim());
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}

public sealed record CliResult(int Code, string Output, string Error)
{
    public JsonElement Json => JsonDocument.Parse(Output).RootElement;

    public string Reason => Json.GetProperty("reason").GetString()!;

    public int[] Seqs => [ .. Json.GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("seq").GetInt32()) ];
}
