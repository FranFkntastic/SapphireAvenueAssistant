using System.Net.Http;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Persistence;
using SapphireAvenue.BridgeProtocol;

namespace SapphireAvenueRelay;

internal sealed class CwlsRelayWorker : IDisposable
{
    private const int MaximumOutboxItems = 512;
    private readonly object gate = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly RelayConfiguration configuration;
    private readonly IChatGui chatGui;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly RelayCoordinatorClient coordinator;
    private readonly CancellationTokenSource cancellation = new();
    private readonly string runtimeInstanceId = Guid.NewGuid().ToString("N");
    private readonly string outboxPath;
    private readonly List<ObservationEnvelope> observations;
    private readonly Task loop;
    private TaskCompletionSource<bool>? pendingEcho;
    private string? pendingEchoText;
    private int? pendingEchoSlot;
    private string? pendingEchoCwlsName;
    private PendingSendSnapshot? pendingSend;
    private bool coordinatorReachable;
    private string role = "offline";
    private bool isPreferred;
    private long epoch;
    private DateTimeOffset? leaseExpiresAtUtc;
    private string? lastError;
    private bool identityConflictDetected;

    public CwlsRelayWorker(
        IDalamudPluginInterface pluginInterface,
        RelayConfiguration configuration,
        IChatGui chatGui,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.configuration = configuration;
        this.chatGui = chatGui;
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.log = log;
        coordinator = new RelayCoordinatorClient(configuration);
        outboxPath = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "observations.json");
        observations = LoadOutbox();
        chatGui.ChatMessage += OnChatMessage;
        loop = Task.Run(() => RunAsync(cancellation.Token));
    }

    public string RuntimeInstanceId => runtimeInstanceId;

    public void MarkDisabled()
    {
        lock (gate)
        {
            identityConflictDetected = false;
            SetDisabledUnsafe();
        }
    }

    public RelaySnapshot CreateSnapshot()
    {
        var slots = CwlsStateReader.ReadSlots();
        var actualName = slots.FirstOrDefault(value => value.Slot == configuration.CwlsSlot)?.Name;
        var slotMatches = IsExactSlotMatch(actualName);
        var sendEligibility = EvaluateLocalSendEligibility(requireDirection: true, slots);
        var player = objectTable.LocalPlayer;
        var identity = ReadGameIdentity(player);
        lock (gate)
        {
            return new RelaySnapshot(
                "sapphire-avenue-relay.snapshot.v1",
                clientState.IsLoggedIn,
                identity.CharacterName,
                identity.HomeWorldName,
                slots,
                configuration.CwlsSlot,
                configuration.ExpectedCwlsName,
                actualName,
                slotMatches,
                configuration.ObserveToDiscordEnabled,
                configuration.DiscordToGameEnabled,
                sendEligibility.Allowed,
                RelayConfigurationPolicy.IsCoordinatorConfigured(configuration),
                coordinatorReachable,
                role,
                isPreferred,
                epoch,
                leaseExpiresAtUtc,
                observations.Count,
                pendingSend,
                lastError);
        }
    }

    public async Task<RelayPairingResult> PairAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var bootstrap = RelayConnectionBootstrap.Parse(connectionString);
        var pairing = await coordinator.PairAsync(
            bootstrap.CoordinatorBaseUri.AbsoluteUri,
            bootstrap.PairingCode,
            cancellationToken).ConfigureAwait(false);
        return new RelayPairingResult(
            bootstrap.CoordinatorBaseUri.AbsoluteUri,
            pairing.NodeId,
            pairing.AccessToken);
    }

    public void ApplyPairing(RelayPairingResult pairing)
    {
        var coordinatorUri = RelayConnectionBootstrap.ParseCoordinatorBaseUri(pairing.CoordinatorBaseUrl);
        if (!RelayConfigurationPolicy.IsNodeIdValid(pairing.NodeId) ||
            !RelayConfigurationPolicy.IsAccessTokenValid(pairing.AccessToken))
        {
            throw new InvalidOperationException("The coordinator returned an invalid node identity or credential.");
        }

        var protectedToken = AgentBridgeDataProtection.ProtectToken(
            pairing.AccessToken,
            configuration.PluginInstanceId + ":relay");
        lock (gate)
        {
            configuration.ObserveToDiscordEnabled = false;
            configuration.DiscordToGameEnabled = false;
            configuration.CoordinatorBaseUrl = coordinatorUri.AbsoluteUri;
            configuration.NodeId = pairing.NodeId;
            configuration.NodeLabel = string.Empty;
            configuration.RelayProtectedAccessToken = protectedToken;
            identityConflictDetected = false;
            SetDisabledUnsafe();
            configuration.Save(pluginInterface);
        }
    }

    public string? SelectCwls(CwlsSlotSnapshot selection)
    {
        var discovered = CwlsStateReader.ReadSlots();
        if (RelayConfigurationPolicy.ResolveSelection(discovered, selection.Slot, selection.Name) is null)
            return "That cross-world linkshell is no longer available. Open the list and select it again.";

        lock (gate)
        {
            configuration.CwlsSlot = selection.Slot;
            configuration.ExpectedCwlsName = selection.Name;
            lastError = null;
            configuration.Save(pluginInterface);
        }

        return null;
    }

    public string? SetDirections(bool observeToDiscord, bool discordToGame)
    {
        var snapshot = CreateSnapshot();
        if ((observeToDiscord || discordToGame) && (!snapshot.SlotMatches || !snapshot.CoordinatorConfigured))
            return "Pair this installation and select the exact current CWLS before enabling relay participation.";

        lock (gate)
        {
            configuration.ObserveToDiscordEnabled = observeToDiscord;
            configuration.DiscordToGameEnabled = discordToGame;
            if (!observeToDiscord && !discordToGame)
                SetDisabledUnsafe();
            configuration.Save(pluginInterface);
        }

        return null;
    }

    public void ClearConfiguration()
    {
        lock (gate)
        {
            configuration.ObserveToDiscordEnabled = false;
            configuration.DiscordToGameEnabled = false;
            configuration.CoordinatorBaseUrl = string.Empty;
            configuration.NodeId = string.Empty;
            configuration.NodeLabel = string.Empty;
            configuration.RelayProtectedAccessToken = string.Empty;
            configuration.CwlsSlot = 0;
            configuration.ExpectedCwlsName = string.Empty;
            identityConflictDetected = false;
            SetDisabledUnsafe();
            configuration.Save(pluginInterface);
        }
    }

    public async Task<RelayTestReceipt> SendTestAsync(string message, CancellationToken cancellationToken)
    {
        var normalized = CwlsChannels.Normalize(message, 300)
            ?? throw new InvalidOperationException("Test message is empty.");
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var eligibility = await framework.RunOnTick(
                () => GetEligibility(requireDirection: false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!eligibility.Allowed)
                return new RelayTestReceipt(false, "not-sent", eligibility.Reason, null, null);

            var echoText = $"[Discord · Relay Test] {normalized}";
            var startedAt = DateTimeOffset.UtcNow;
            DeliveryOutcome outcome;
            string detail;
            try
            {
                var attempt = await SendAndConfirmEchoAsync(
                    echoText,
                    "local-test",
                    "local-test",
                    requireDirection: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                outcome = attempt.Outcome;
                detail = outcome == DeliveryOutcome.Sent
                    ? "Matching CWLS echo observed."
                    : attempt.Detail;
            }
            catch (ArgumentException exception)
            {
                outcome = DeliveryOutcome.NotSent;
                detail = $"The game rejected the message before submission: {exception.Message}";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                outcome = DeliveryOutcome.Ambiguous;
                detail = $"The send outcome is uncertain: {exception.Message}";
            }
            return new RelayTestReceipt(
                outcome == DeliveryOutcome.Sent,
                ToWireName(outcome),
                detail,
                echoText,
                startedAt);
        }
        finally
        {
            sendGate.Release();
        }
    }

    public void Dispose()
    {
        chatGui.ChatMessage -= OnChatMessage;
        cancellation.Cancel();
        lock (gate)
        {
            pendingEcho?.TrySetCanceled();
            TryPersistOutbox();
        }
        _ = loop.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                coordinator.Dispose();
                sendGate.Dispose();
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!RelayConfigurationPolicy.IsCoordinatorConfigured(configuration))
                {
                    SetDisconnected("Relay coordinator is not configured.");
                }
                else if (HasIdentityConflict())
                {
                    // The coordinator revoked this credential. A new pairing is required;
                    // retrying would only hammer a permanently fenced identity.
                }
                else
                {
                    var cycle = await framework.RunOnTick(
                        () => new RelayCycleContext(
                            GetEligibility(requireDirection: true),
                            ReadGameIdentity(objectTable.LocalPlayer)),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    var heartbeat = await coordinator.HeartbeatAsync(
                        runtimeInstanceId,
                        cycle.SendEligibility.Allowed,
                        cycle.Identity,
                        cancellationToken).ConfigureAwait(false);
                    lock (gate)
                    {
                        coordinatorReachable = true;
                        role = heartbeat.Role;
                        isPreferred = heartbeat.IsPreferred;
                        epoch = heartbeat.Epoch;
                        leaseExpiresAtUtc = heartbeat.ExpiresAtUtc;
                        lastError = null;
                        if (configuration.DiscordToGameEnabled && !cycle.SendEligibility.Allowed)
                            lastError = cycle.SendEligibility.Reason;
                    }

                    await FlushObservationsAsync(cancellationToken).ConfigureAwait(false);
                    if (configuration.DiscordToGameEnabled &&
                        cycle.SendEligibility.Allowed &&
                        string.Equals(heartbeat.Role, "leader", StringComparison.OrdinalIgnoreCase))
                    {
                        await ClaimAndSendAsync(heartbeat.Epoch, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (NodeIdentityConflictException exception)
            {
                lock (gate)
                {
                    identityConflictDetected = true;
                    coordinatorReachable = true;
                    role = "identity-conflict";
                    isPreferred = false;
                    leaseExpiresAtUtc = null;
                    lastError = exception.Message;
                }
                log.Error(exception, "Relay pairing was revoked because its character and home world are already claimed.");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException)
            {
                SetDisconnected(exception.Message);
                log.Warning(exception, "Sapphire relay cycle failed closed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task FlushObservationsAsync(CancellationToken cancellationToken)
    {
        while (configuration.ObserveToDiscordEnabled)
        {
            ObservationEnvelope? observation;
            bool mayReport;
            lock (gate)
            {
                observation = observations.FirstOrDefault();
                mayReport = observation is not null && RelayConfigurationPolicy.MaySubmitObservation(
                    observation,
                    runtimeInstanceId,
                    epoch,
                    role,
                    leaseExpiresAtUtc,
                    DateTimeOffset.UtcNow);
            }
            if (observation is null)
                return;

            if (mayReport)
            {
                await coordinator.PostObservationAsync(observation, cancellationToken).ConfigureAwait(false);
            }
            lock (gate)
            {
                if (observations.Count > 0 && observations[0].ObservationId == observation.ObservationId)
                {
                    observations.RemoveAt(0);
                    TryPersistOutbox();
                }
            }
        }
    }

    private async Task ClaimAndSendAsync(long leaseEpoch, CancellationToken cancellationToken)
    {
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var eligibility = await framework.RunOnTick(
                () => GetEligibility(requireDirection: true),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!eligibility.Allowed)
            {
                lock (gate) lastError = eligibility.Reason;
                return;
            }

            var message = await coordinator.ClaimAsync(runtimeInstanceId, leaseEpoch, cancellationToken).ConfigureAwait(false);
            if (message is null)
                return;

            DeliveryOutcome outcome;
            try
            {
                var echoText = CwlsChannels.FormatDiscordLine(message.DiscordDisplayName, message.Content);
                var attempt = await SendAndConfirmEchoAsync(
                    echoText,
                    message.MessageId,
                    message.ClaimId,
                    requireDirection: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                outcome = attempt.Outcome;
                if (outcome != DeliveryOutcome.Sent)
                {
                    lock (gate) lastError = attempt.Detail;
                }
            }
            catch (ArgumentException exception)
            {
                lock (gate) lastError = exception.Message;
                outcome = DeliveryOutcome.NotSent;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (gate) lastError = exception.Message;
                outcome = DeliveryOutcome.Ambiguous;
            }

            using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await coordinator.CompleteAsync(
                    runtimeInstanceId,
                    leaseEpoch,
                    message,
                    outcome,
                    completionTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lock (gate) lastError = $"Game outcome is {ToWireName(outcome)}, but coordinator completion failed: {exception.Message}";
            }
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task<SendAttemptResult> SendAndConfirmEchoAsync(
        string echoText,
        string messageId,
        string claimId,
        bool requireDirection,
        CancellationToken cancellationToken)
    {
        var echo = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pendingEcho = echo;
            pendingEchoText = echoText;
            pendingSend = new PendingSendSnapshot(messageId, claimId, echoText, DateTimeOffset.UtcNow);
        }

        try
        {
            var submission = await framework.RunOnTick(
                () => SubmitIfEligible(echoText, requireDirection),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!submission.Submitted)
                return new SendAttemptResult(DeliveryOutcome.NotSent, submission.Detail);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await echo.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                return new SendAttemptResult(DeliveryOutcome.Sent, "Matching CWLS echo observed.");
            }
            catch (OperationCanceledException)
            {
                return new SendAttemptResult(
                    DeliveryOutcome.Ambiguous,
                    "The message was submitted, but no authoritative CWLS echo was observed.");
            }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pendingEcho, echo))
                {
                    pendingEcho = null;
                    pendingEchoText = null;
                    pendingEchoSlot = null;
                    pendingEchoCwlsName = null;
                    pendingSend = null;
                }
            }
        }
    }

    private SendSubmission SubmitIfEligible(string echoText, bool requireDirection)
    {
        var eligibility = GetEligibility(requireDirection);
        if (!eligibility.Allowed ||
            eligibility.VerifiedSlot is not { } verifiedSlot ||
            eligibility.VerifiedName is not { } verifiedName)
            return new SendSubmission(false, eligibility.Reason);

        // The game state, exact slot/name check, captured slot, and submit all occur in this
        // one framework callback. Mutable configuration is never read after verification.
        lock (gate)
        {
            pendingEchoSlot = verifiedSlot;
            pendingEchoCwlsName = verifiedName;
        }
        Chat.SendMessage($"/cwl{verifiedSlot} {echoText}");
        return new SendSubmission(true, "Message submitted; waiting for the authoritative CWLS echo.");
    }

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        var slot = CwlsChannels.ToSlot(chatMessage.LogKind);
        if (slot is null)
            return;

        var actualName = CwlsStateReader.ReadSlots().FirstOrDefault(value => value.Slot == slot.Value)?.Name;

        var senderBytes = chatMessage.OriginalSender.Data.ToArray();
        var messageBytes = chatMessage.OriginalMessage.Data.ToArray();
        var senderString = chatMessage.OriginalSender.ToDalamudString();
        var player = senderString.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        var senderName = CwlsChannels.Normalize(player?.PlayerName ?? senderString.TextValue, 128) ?? "Unknown";
        var senderWorld = CwlsChannels.Normalize(player?.World.ValueNullable?.Name.ToString(), 64);
        var content = CwlsChannels.Normalize(chatMessage.OriginalMessage.ExtractText(), 500);
        if (content is null)
            return;

        TaskCompletionSource<bool>? echo = null;
        lock (gate)
        {
            if (pendingEcho is not null &&
                pendingEchoSlot == slot.Value &&
                string.Equals(pendingEchoCwlsName, actualName, StringComparison.Ordinal) &&
                string.Equals(pendingEchoText, content, StringComparison.Ordinal) &&
                IsLocalPlayer(senderName))
            {
                echo = pendingEcho;
            }

            if (configuration.ObserveToDiscordEnabled &&
                slot.Value == configuration.CwlsSlot &&
                IsExactSlotMatch(actualName) &&
                RelayConfigurationPolicy.MayReportObservation(
                    role,
                    leaseExpiresAtUtc,
                    DateTimeOffset.UtcNow))
            {
                var observedAt = chatMessage.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(chatMessage.Timestamp)
                    : DateTimeOffset.UtcNow;
                var observation = new ObservationEnvelope(
                    CwlsChannels.ObservationId(slot.Value, chatMessage.Timestamp, senderBytes, messageBytes),
                    slot.Value,
                    senderName,
                    senderWorld,
                    content,
                    observedAt,
                    runtimeInstanceId,
                    epoch);
                if (observations.All(item => item.ObservationId != observation.ObservationId))
                {
                    if (observations.Count >= MaximumOutboxItems)
                    {
                        lastError = "Observation outbox is full; relay capture is paused rather than discarding older lines.";
                    }
                    else
                    {
                        observations.Add(observation);
                        TryPersistOutbox();
                    }
                }
            }
        }

        echo?.TrySetResult(true);
    }

    private RelaySendEligibility GetEligibility(bool requireDirection) =>
        EvaluateLocalSendEligibility(requireDirection, CwlsStateReader.ReadSlots());

    private RelaySendEligibility EvaluateLocalSendEligibility(
        bool requireDirection,
        IReadOnlyList<CwlsSlotSnapshot> discoveredSlots) =>
        RelayConfigurationPolicy.EvaluateSendEligibility(
            requireDirection,
            configuration.DiscordToGameEnabled,
            clientState.IsLoggedIn,
            objectTable.LocalPlayer is not null,
            configuration.CwlsSlot,
            configuration.ExpectedCwlsName,
            discoveredSlots);

    private bool IsExactSlotMatch(string? actualName) =>
        !string.IsNullOrWhiteSpace(actualName) &&
        string.Equals(actualName, configuration.ExpectedCwlsName, StringComparison.Ordinal);

    private bool IsLocalPlayer(string senderName) =>
        string.Equals(objectTable.LocalPlayer?.Name.TextValue, senderName, StringComparison.Ordinal);

    private bool HasIdentityConflict()
    {
        lock (gate)
            return identityConflictDetected;
    }

    private static RelayGameIdentity ReadGameIdentity(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter? player)
    {
        if (player is null)
            return new RelayGameIdentity(null, null, null);

        var characterName = CwlsChannels.Normalize(player.Name.TextValue, 64);
        var homeWorldName = CwlsChannels.Normalize(player.HomeWorld.ValueNullable?.Name.ToString(), 64);
        return characterName is null || homeWorldName is null || player.HomeWorld.RowId == 0
            ? new RelayGameIdentity(null, null, null)
            : new RelayGameIdentity(characterName, player.HomeWorld.RowId, homeWorldName);
    }

    private List<ObservationEnvelope> LoadOutbox()
    {
        if (!File.Exists(outboxPath))
            return [];
        try
        {
            return AtomicJsonFile.Read<ObservationOutbox>(outboxPath, RelayJsonContext.Default.Options)?.Items ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            var quarantine = $"{outboxPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(outboxPath, quarantine, overwrite: false);
            lastError = $"A corrupt observation outbox was quarantined as {Path.GetFileName(quarantine)}.";
            log.Error(exception, "Sapphire relay observation outbox was corrupt and has been quarantined.");
            return [];
        }
    }

    private void TryPersistOutbox()
    {
        try
        {
            AtomicJsonFile.Write(outboxPath, new ObservationOutbox([.. observations]), RelayJsonContext.Default.Options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lastError = $"Observation outbox could not be persisted: {exception.Message}";
            log.Error(exception, "Sapphire relay observation outbox persistence failed.");
        }
    }

    private void SetDisconnected(string error)
    {
        lock (gate)
        {
            coordinatorReachable = false;
            role = "offline";
            isPreferred = false;
            leaseExpiresAtUtc = null;
            lastError = error;
        }
    }

    private void SetDisabledUnsafe()
    {
        coordinatorReachable = false;
        role = "disabled";
        isPreferred = false;
        epoch = 0;
        leaseExpiresAtUtc = null;
        lastError = null;
    }

    private static string ToWireName(DeliveryOutcome outcome) => outcome switch
    {
        DeliveryOutcome.Sent => "sent",
        DeliveryOutcome.NotSent => "not-sent",
        _ => "ambiguous",
    };

    private sealed record SendSubmission(bool Submitted, string Detail);
    private sealed record SendAttemptResult(DeliveryOutcome Outcome, string Detail);
    private sealed record RelayCycleContext(RelaySendEligibility SendEligibility, RelayGameIdentity Identity);
}

internal sealed record RelayTestReceipt(
    bool Success,
    string Outcome,
    string Message,
    string? EchoText,
    DateTimeOffset? StartedAtUtc);
