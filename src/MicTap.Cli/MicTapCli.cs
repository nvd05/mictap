using System.CommandLine;
using System.Globalization;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using MicTap.Hub.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace MicTap.Cli;

/// <summary>The mictap command for agents. It talks to the hub inside the running mictap app; every command prints one JSON line.</summary>
public static class MicTapCli
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int NotFound = 2;
    public const int HubDown = 3;
    public const int BadArguments = 4;
    public const int Superseded = 5;

    private const string WaitHelp =
        "Wait for new transcribed speech, print it as one JSON line and exit. Use it as a loop: call `mictap wait`, " +
        "process the segments, call it again — each call continues after the last segment you received (the cursor is " +
        "kept per --agent in the session's agents folder). Without --min-words and --max-wait it returns at the first " +
        "new segment, or at once if unread segments exist. --min-words N returns when the unread text has N words; " +
        "--max-wait SEC returns after SEC seconds with whatever accumulated; with both, whichever comes first. It also " +
        "returns when the recording stops (reason session_stopped, once its last phrases are transcribed, so they are in the " +
        "reply) and when the user presses «send to agent now» in mictap " +
        "(reason force_push, even below --min-words): the user has finished the thought and waits for your answer — answer " +
        "right away and call wait again only after that. A press while no wait runs makes the next wait return at once " +
        "with force_push. Without --project and --session it follows the active recording; with --project alone, the " +
        "recording of that project. A new wait of the same --agent on the same session takes over an older one still " +
        "running: the older exits with code 5 and leaves the cursor alone. Behind a tool call with a time limit keep " +
        "--max-wait well below it — 45 or less through Claude Desktop, which gives up on a call after 60 s: a wait that " +
        "outlives the call and ends before your next one moves the cursor past segments you never saw. " +
        "Output: {project, session, session_started, reason, from_seq, to_seq, lang_now, " +
        "segments:[{seq,start,end,start_at,end_at,lang,text}]}; reason is first_event, min_words, max_wait, session_stopped " +
        "or force_push; start/end are seconds from the session start, session_started/start_at/end_at the clock time in " +
        "ISO 8601 with the time zone (which day and hour it was said).";

    private const string ExitCodes =
        "Exit codes: 0 ok, 2 no such project or session (or nothing recording), 3 mictap is not running, 4 bad arguments, " +
        "5 a newer wait of the same agent took over.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(string[] args, int port, TextWriter output, TextWriter error, CancellationToken cancellation)
    {
        var agent = new Option<string>("--agent")
        {
            Description = "Your name; presence and the read cursor are kept per agent.",
            DefaultValueFactory = _ => "claude",
        };
        var project = new Option<string?>("--project") { Description = "Project name (as shown in mictap); alone, its running recording." };
        var session = new Option<string?>("--session") { Description = "Session name." };
        var minWords = new Option<int?>("--min-words") { Description = "Return when the unread text has this many words." };
        var maxWait = new Option<double?>("--max-wait")
        {
            Description = "Return after this many seconds with what has accumulated (45 or less through Claude Desktop).",

            // "0.5" whatever the Windows locale: agents write numbers the invariant way.
            CustomParser = result =>
            {
                if (double.TryParse(result.Tokens[ 0 ].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                {
                    return seconds;
                }

                result.AddError($"--max-wait: '{result.Tokens[ 0 ].Value}' is not a number of seconds.");
                return null;
            },
        };
        var fromStart = new Option<bool>("--from-start") { Description = "Forget the cursor and read the session from its first segment." };
        var skipBacklog = new Option<bool>("--skip-backlog")
        {
            Description = "A session this agent has never read counts as read up to now: only what is said from here on comes. "
                + "For a listener that follows a project and should not get its old sessions all at once.",
        };
        minWords.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is <= 0)
            {
                result.AddError("--min-words must be a positive number.");
            }
        });
        maxWait.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<double?>() is <= 0)
            {
                result.AddError("--max-wait must be a positive number of seconds.");
            }
        });

        var wait = new Command("wait", WaitHelp + " " + ExitCodes) { project, session, agent, minWords, maxWait, fromStart, skipBacklog };
        wait.SetAction((parse, token) => CallAsync(port, parse.GetValue(agent)!, output, error, async hub =>
            await hub.InvokeAsync<WaitReply>(
                AgentHub.Wait,
                new WaitRequest(parse.GetValue(project), parse.GetValue(session), parse.GetValue(agent)!,
                    parse.GetValue(minWords), parse.GetValue(maxWait), parse.GetValue(fromStart), parse.GetValue(skipBacklog)),
                token)));

        var status = new Command("status", "The running recording (project, session, language now) and the agents.") { agent };
        status.SetAction((parse, token) => CallAsync(port, parse.GetValue(agent)!, output, error, async hub =>
            await hub.InvokeAsync<StatusReply>(AgentHub.Status, token)));

        var projects = new Command("projects", "Projects with their languages, audio retention and number of sessions.") { agent };
        projects.SetAction((parse, token) => CallAsync(port, parse.GetValue(agent)!, output, error, async hub =>
            await hub.InvokeAsync<IReadOnlyList<ProjectDto>>(AgentHub.Projects, token)));

        var projectRequired = new Option<string>("--project") { Description = "Project name.", Required = true };
        var sessions = new Command("sessions", "Sessions of a project, the most recently recorded first.") { projectRequired, agent };
        sessions.SetAction((parse, token) => CallAsync(port, parse.GetValue(agent)!, output, error, async hub =>
            await hub.InvokeAsync<IReadOnlyList<SessionDto>>(AgentHub.Sessions, parse.GetValue(projectRequired), token)));

        var sessionRequired = new Option<string>("--session") { Description = "Session name.", Required = true };
        var seq = new Option<int>("--seq") { Description = "First seq to print.", DefaultValueFactory = _ => 1 };
        var tail = new Command(
            "tail",
            "Segments of a session from --seq on, without moving any cursor. Output: {project, session, session_started, "
            + "segments:[{seq,start,end,start_at,end_at,lang,text}]}; start/end are seconds from the session start, "
            + "session_started/start_at/end_at the clock time in ISO 8601 with the time zone.")
        {
            sessionRequired, project, seq, agent,
        };
        tail.SetAction((parse, token) => CallAsync(port, parse.GetValue(agent)!, output, error, async hub =>
            await hub.InvokeAsync<TailReply>(
                AgentHub.Tail, new TailRequest(parse.GetValue(project), parse.GetValue(sessionRequired)!, parse.GetValue(seq)), token)));

        var root = new RootCommand(
            "mictap — read what the mictap app transcribes, for agents. Start with `mictap wait --help`. " + ExitCodes)
        {
            wait, status, projects, sessions, tail,
        };

        ParseResult parsed = root.Parse(args);
        if (parsed.Errors.Count > 0)
        {
            foreach (System.CommandLine.Parsing.ParseError parseError in parsed.Errors)
            {
                await error.WriteLineAsync(parseError.Message);
            }

            await error.WriteLineAsync("See: mictap --help");
            return BadArguments;
        }

        return await parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellation);
    }

    private static async Task<int> CallAsync<T>(int port, string agent, TextWriter output, TextWriter error, Func<HubConnection, Task<T>> call)
    {
        await using HubConnection hub = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{port}{AgentHub.Path}?{AgentHub.AgentQuery}={Uri.EscapeDataString(agent)}")
            .Build();
        try
        {
            await hub.StartAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
        {
            await error.WriteLineAsync($"mictap is not running: no hub at 127.0.0.1:{port} ({ex.Message}).");
            return HubDown;
        }

        try
        {
            T reply = await call(hub);
            await output.WriteLineAsync(JsonSerializer.Serialize(reply, JsonOptions));
            return Ok;
        }
        catch (HubException ex) when (ex.Message.Contains(AgentHub.NotFound, StringComparison.Ordinal))
        {
            await error.WriteLineAsync(After(ex.Message, AgentHub.NotFound));
            return NotFound;
        }
        catch (HubException ex) when (ex.Message.Contains(AgentHub.Superseded, StringComparison.Ordinal))
        {
            await error.WriteLineAsync(After(ex.Message, AgentHub.Superseded));
            return Superseded;
        }
        catch (HubException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return Failed;
        }
    }

    /// <summary>The message after the prefix the hub marks it with; SignalR puts its own words in front.</summary>
    private static string After(string message, string prefix) =>
        message[ (message.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length).. ].Trim();
}
