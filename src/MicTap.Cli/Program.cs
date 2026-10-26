using System.Globalization;
using System.Text.Json;
using MicTap.Cli;
using MicTap.Hub.Contracts;

// Agents read the help: English whatever the Windows language.
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await MicTapCli.RunAsync(args, HubPort(), Console.Out, Console.Error, cancellation.Token);

// The app's settings.json says where its hub listens; the default otherwise.
static int HubPort()
{
    string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mictap", "settings.json");
    if (!File.Exists(settings))
    {
        return AgentHub.DefaultPort;
    }

    using JsonDocument json = JsonDocument.Parse(File.ReadAllText(settings));
    return json.RootElement.TryGetProperty("hubPort", out JsonElement port) && port.TryGetInt32(out int value) ? value : AgentHub.DefaultPort;
}
