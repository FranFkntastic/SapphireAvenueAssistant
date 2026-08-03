using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using SharedAgentBridgeHost = Franthropy.Dalamud.AgentBridge.AgentBridgeHost;

namespace SapphireAvenueRelay;

internal sealed class RelayAgentBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly RelayConfiguration configuration;
    private readonly IFramework framework;
    private readonly CwlsRelayWorker worker;
    private readonly AgentBridgeCommandRouter router = new();
    private readonly SharedAgentBridgeHost host;
    private readonly AgentBridgeRuntimeIdentity runtime;
    private readonly (string Id, string Alias) profile;

    public RelayAgentBridge(
        IDalamudPluginInterface pluginInterface,
        RelayConfiguration configuration,
        IFramework framework,
        CwlsRelayWorker worker)
    {
        this.pluginInterface = pluginInterface;
        this.configuration = configuration;
        this.framework = framework;
        this.worker = worker;
        profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(pluginInterface.GetPluginConfigDirectory());
        runtime = AgentBridgeRuntimeIdentity.FromAssembly(
            "SapphireAvenueRelay",
            Assembly.GetExecutingAssembly(),
            pluginInterface.AssemblyLocation.FullName);

        router.Register("get-snapshot", GetSnapshotAsync);
        router.Register("configure-relay", ConfigureAsync);
        router.Register("clear-relay", ClearRelayAsync);
        router.Register("set-directions", SetDirectionsAsync);
        router.Register("send-test", SendTestAsync);

        host = new SharedAgentBridgeHost(new AgentBridgeHostOptions
        {
            ConfigDirectory = pluginInterface.GetPluginConfigDirectory(),
            PluginInstanceId = configuration.PluginInstanceId,
            PipeName = $"SapphireAvenueRelay.AgentBridge.{Environment.ProcessId}.{configuration.PluginInstanceId}",
            GetProtectedAccessToken = () => configuration.AgentBridgeProtectedAccessToken,
            SetProtectedAccessToken = value => configuration.AgentBridgeProtectedAccessToken = value,
            SaveConfiguration = Save,
            CreateManifest = CreateManifest,
            HandleRequestAsync = router.HandleAsync,
            RequestTimeout = TimeSpan.FromSeconds(20),
        });
        host.Start();
    }

    public void Dispose() => host.Dispose();

    private AgentBridgeManifest CreateManifest() => new(
        1,
        runtime,
        profile.Id,
        profile.Alias,
        "sapphire-avenue-relay.snapshot.v1",
        [
            new AgentBridgeCapabilityDescriptor("snapshot.read"),
            new AgentBridgeCapabilityDescriptor("relay.configure"),
            new AgentBridgeCapabilityDescriptor("relay.test-send"),
        ],
        [],
        [],
        [
            new AgentBridgeActionDescriptor(
                "configure-relay",
                "Configure relay node",
                "relay",
                AgentBridgeUiControlKind.Input,
                true),
            new AgentBridgeActionDescriptor(
                "set-directions",
                "Set relay directions",
                "relay",
                AgentBridgeUiControlKind.Toggle,
                true),
            new AgentBridgeActionDescriptor(
                "clear-relay",
                "Clear relay configuration",
                "relay",
                AgentBridgeUiControlKind.Button,
                true),
            new AgentBridgeActionDescriptor(
                "send-test",
                "Send verified CWLS test",
                "relay",
                AgentBridgeUiControlKind.Button,
                true),
        ]);

    private async ValueTask<AgentBridgeResponse> GetSnapshotAsync(AgentBridgeRequest _, CancellationToken cancellationToken)
    {
        var snapshot = await framework.RunOnTick(worker.CreateSnapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
        return AgentBridgeResponse.Ok("Relay snapshot captured.", snapshot);
    }

    private async ValueTask<AgentBridgeResponse> ConfigureAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        var arguments = Deserialize<ConfigureRelayArguments>(request.Arguments);
        if (arguments is null)
            return AgentBridgeResponse.Fail("Coordinator URL, node ID, CWLS slot, and expected CWLS name are required.");

        try
        {
            var coordinatorUri = RelayCoordinatorClient.ValidateBaseUri(arguments.CoordinatorBaseUrl ?? string.Empty);
            var nodeId = arguments.NodeId?.Trim() ?? string.Empty;
            var expectedName = arguments.ExpectedCwlsName?.Trim() ?? string.Empty;
            if (!RelayConfigurationPolicy.IsNodeIdValid(nodeId))
                return AgentBridgeResponse.Fail("Node ID is invalid.");
            if (arguments.CwlsSlot is < 1 or > 8 || string.IsNullOrWhiteSpace(expectedName))
                return AgentBridgeResponse.Fail("CWLS slot must be 1-8 and expected name must be explicit.");
            if (string.IsNullOrWhiteSpace(arguments.NodeToken) && string.IsNullOrWhiteSpace(configuration.RelayProtectedAccessToken))
                return AgentBridgeResponse.Fail("A node token is required for initial configuration.");

            await framework.RunOnTick(() =>
            {
                configuration.CoordinatorBaseUrl = coordinatorUri.AbsoluteUri;
                configuration.NodeId = nodeId;
                configuration.NodeLabel = string.Empty;
                configuration.CwlsSlot = arguments.CwlsSlot;
                configuration.ExpectedCwlsName = expectedName;
                if (!string.IsNullOrWhiteSpace(arguments.NodeToken))
                {
                    configuration.RelayProtectedAccessToken = AgentBridgeDataProtection.ProtectToken(
                        arguments.NodeToken,
                        configuration.PluginInstanceId + ":relay");
                }
                configuration.ObserveToDiscordEnabled = false;
                configuration.DiscordToGameEnabled = false;
                Save();
                worker.MarkDisabled();
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            var snapshot = await framework.RunOnTick(worker.CreateSnapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
            return snapshot.SlotMatches
                ? AgentBridgeResponse.Ok("Relay configured with both directions disabled; CWLS identity matches.", snapshot)
                : new AgentBridgeResponse
                {
                    Success = false,
                    Message = "Relay configuration was saved disabled, but the current CWLS slot does not match.",
                    Receipt = snapshot,
                };
        }
        catch (InvalidOperationException exception)
        {
            return AgentBridgeResponse.Fail(exception.Message);
        }
    }

    private async ValueTask<AgentBridgeResponse> SetDirectionsAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        var arguments = Deserialize<SetDirectionsArguments>(request.Arguments);
        if (arguments is null)
            return AgentBridgeResponse.Fail("Both relay direction flags are required.");

        var snapshot = await framework.RunOnTick(worker.CreateSnapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
        if ((arguments.ObserveToDiscord || arguments.DiscordToGame) && (!snapshot.SlotMatches || !snapshot.CoordinatorConfigured))
            return AgentBridgeResponse.Fail("Relay cannot be enabled until the coordinator and exact CWLS identity are verified.");

        await framework.RunOnTick(() =>
        {
            configuration.ObserveToDiscordEnabled = arguments.ObserveToDiscord;
            configuration.DiscordToGameEnabled = arguments.DiscordToGame;
            Save();
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return AgentBridgeResponse.Ok("Relay directions updated.", await framework.RunOnTick(worker.CreateSnapshot, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<AgentBridgeResponse> ClearRelayAsync(AgentBridgeRequest _, CancellationToken cancellationToken)
    {
        await framework.RunOnTick(() =>
        {
            configuration.ObserveToDiscordEnabled = false;
            configuration.DiscordToGameEnabled = false;
            configuration.CoordinatorBaseUrl = string.Empty;
            configuration.NodeId = string.Empty;
            configuration.NodeLabel = string.Empty;
            configuration.RelayProtectedAccessToken = string.Empty;
            configuration.CwlsSlot = 0;
            configuration.ExpectedCwlsName = string.Empty;
            Save();
            worker.MarkDisabled();
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return AgentBridgeResponse.Ok(
            "Relay configuration and protected node credential cleared; both directions remain disabled.",
            await framework.RunOnTick(worker.CreateSnapshot, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<AgentBridgeResponse> SendTestAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        var arguments = Deserialize<SendTestArguments>(request.Arguments);
        if (arguments is null || string.IsNullOrWhiteSpace(arguments.Message))
            return AgentBridgeResponse.Fail("A non-empty test message is required.");
        var receipt = await worker.SendTestAsync(arguments.Message, cancellationToken).ConfigureAwait(false);
        return receipt.Success
            ? AgentBridgeResponse.Ok(receipt.Message, receipt)
            : new AgentBridgeResponse { Success = false, Message = receipt.Message, Receipt = receipt };
    }

    private static T? Deserialize<T>(JsonElement? arguments) =>
        arguments is null ? default : arguments.Value.Deserialize<T>(JsonOptions);

    private void Save() => configuration.Save(pluginInterface);
}
