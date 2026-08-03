using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;

namespace SapphireAvenueRelay;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/sadbridge";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly CwlsRelayWorker worker;
    private readonly RelayAgentBridge bridge;
    private readonly WindowSystem windowSystem = new("SapphireAvenueDiscordBridge");
    private readonly RelayConfigurationWindow configurationWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IChatGui chatGui,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICommandManager commandManager,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        ECommonsMain.Init(pluginInterface, this);
        var configuration = pluginInterface.GetPluginConfig() as RelayConfiguration ?? new RelayConfiguration();
        configuration.Version = 2;
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
        configurationWindow = new RelayConfigurationWindow(configuration, worker);
        windowSystem.AddWindow(configurationWindow);
        commandManager.AddHandler(CommandName, new CommandInfo((_, _) => OpenConfiguration())
        {
            HelpMessage = "Open Sapphire Avenue Discord Bridge configuration.",
        });
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfiguration;
        log.Information("Sapphire Avenue Discord Bridge loaded disabled-by-default with authenticated bridge discovery.");
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfiguration;
        commandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        configurationWindow.Dispose();
        bridge.Dispose();
        worker.Dispose();
        ECommonsMain.Dispose();
    }

    private void DrawUi() => windowSystem.Draw();

    private void OpenConfiguration() => configurationWindow.IsOpen = true;
}
