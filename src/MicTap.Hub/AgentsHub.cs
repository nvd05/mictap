using MicTap.Hub.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace MicTap.Hub;

/// <summary>SignalR hub /agents: what the mictap CLI calls. An open connection is the agent's presence.</summary>
internal sealed class AgentsHub(AgentsState state) : Microsoft.AspNetCore.SignalR.Hub
{
    private string Agent =>
        Context.GetHttpContext()?.Request.Query[AgentHub.AgentQuery].ToString() is { Length: > 0 } name ? name : "agent";

    public override Task OnConnectedAsync()
    {
        state.Connected(Agent, Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        state.Disconnected(Agent, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public StatusReply Status() => state.Status();

    public IReadOnlyList<ProjectDto> Projects() => state.ListProjects();

    public IReadOnlyList<SessionDto> Sessions(string project) => state.ListSessions(project);

    public IReadOnlyList<SegmentDto> Tail(TailRequest request) => state.Tail(request);

    public Task<WaitReply> Wait(WaitRequest request) =>
        state.WaitAsync(request with { Agent = Agent }, Context.ConnectionId, Context.ConnectionAborted);
}
