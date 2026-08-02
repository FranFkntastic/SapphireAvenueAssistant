using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace SapphireAvenueRelay;

internal static unsafe class CwlsStateReader
{
    public static IReadOnlyList<CwlsSlotSnapshot> ReadSlots()
    {
        var module = InfoModule.Instance();
        if (module is null)
            return [];

        var proxy = (InfoProxyCrossWorldLinkshell*)module->GetInfoProxyById(InfoProxyId.CrossWorldLinkshell);
        if (proxy is null)
            return [];

        var slots = new List<CwlsSlotSnapshot>(8);
        for (var index = 0; index < 8; index++)
        {
            var name = proxy->GetCrossworldLinkshellName((uint)index);
            var value = name is null ? string.Empty : name->ToString();
            slots.Add(new CwlsSlotSnapshot(index + 1, value));
        }

        return slots;
    }
}
