using System.Net;
using MicTap.Hub.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MicTap.Hub;

/// <summary>
/// Kestrel with the SignalR hub inside the app process, on 127.0.0.1 only and without authentication.
/// Started and stopped with the app.
/// </summary>
public sealed class HubHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private HubHost(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    /// <param name="port">0 picks a free port (tests).</param>
    /// <exception cref="IOException">The port is taken.</exception>
    public static async Task<HubHost> StartAsync(int port, AgentsState state)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(state);
        WebApplication app = builder.Build();
        app.MapHub<AgentsHub>(AgentHub.Path);
        try
        {
            await app.StartAsync();
        }
        catch (IOException)
        {
            await app.DisposeAsync();
            throw;
        }

        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HubHost(app, new Uri(address).Port);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
