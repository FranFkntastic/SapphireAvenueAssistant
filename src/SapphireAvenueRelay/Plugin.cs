using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;

namespace SapphireAvenueRelay;

public sealed class Plugin : IDalamudPlugin
{
    private readonly CwlsRelayWorker worker;
    private readonly RelayAgentBridge bridge;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IChatGui chatGui,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        IPluginLog log)
    {
        ECommonsMain.Init(pluginInterface, this);
        var configuration = pluginInterface.GetPluginConfig() as RelayConfiguration ?? new RelayConfiguration();
        configuration.Save(pluginInterface);
        worker = new CwlsRelayWorker(
            pluginInterface,
            configuration,
            chatGui,
            framework,
            clientState,
            objectTable,
            log);
        bridge = new RelayAgentBridge(pluginInterface, configuration, framework, worker);
        log.Information("Sapphire Avenue Relay loaded disabled-by-default with authenticated bridge discovery.");
    }

    public void Dispose()
    {
        bridge.Dispose();
        worker.Dispose();
        ECommonsMain.Dispose();
    }
}
