using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SapphireAvenueRelay;

public sealed class RelayConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public string PluginInstanceId { get; set; } = Guid.NewGuid().ToString("N");

    public string AgentBridgeProtectedAccessToken { get; set; } = string.Empty;

    public string CoordinatorBaseUrl { get; set; } = string.Empty;

    public string NodeId { get; set; } = string.Empty;

    public string NodeLabel { get; set; } = string.Empty;

    public string RelayProtectedAccessToken { get; set; } = string.Empty;

    public int CwlsSlot { get; set; }

    public string ExpectedCwlsName { get; set; } = string.Empty;

    public bool ObserveToDiscordEnabled { get; set; }

    public bool DiscordToGameEnabled { get; set; }

    public void Save(IDalamudPluginInterface pluginInterface) => pluginInterface.SavePluginConfig(this);
}
