using System.Net.Http;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation;
using Franthropy.Dalamud.Persistence;

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
    private PendingSendSnapshot? pendingSend;
    private bool coordinatorReachable;
    private string role = "offline";
    private long epoch;
    private DateTimeOffset? leaseExpiresAtUtc;
    private string? lastError;

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

    public void MarkDisabled() => SetDisabled();

    public RelaySnapshot CreateSnapshot()
    {
        var slots = CwlsStateReader.ReadSlots();
        var actualName = slots.FirstOrDefault(value => value.Slot == configuration.CwlsSlot)?.Name;
        var slotMatches = IsExactSlotMatch(actualName);
        var player = objectTable.LocalPlayer;
        lock (gate)
        {
            return new RelaySnapshot(
                "sapphire-avenue-relay.snapshot.v1",
                clientState.IsLoggedIn,
                player?.Name.TextValue,
                slots,
                configuration.CwlsSlot,
                configuration.ExpectedCwlsName,
                actualName,
                slotMatches,
                configuration.ObserveToDiscordEnabled,
                configuration.DiscordToGameEnabled,
                IsCoordinatorConfigured(),
                coordinatorReachable,
                role,
                epoch,
                leaseExpiresAtUtc,
                observations.Count,
                pendingSend,
                lastError);
        }
    }

    public async Task<RelayTestReceipt> SendTestAsync(string message, CancellationToken cancellationToken)
    {
        var normalized = CwlsChannels.Normalize(message, 300)
            ?? throw new InvalidOperationException("Test message is empty.");
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var eligibility = await framework.RunOnTick(GetEligibility, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!eligibility.Allowed)
                return new RelayTestReceipt(false, "not-sent", eligibility.Reason, null, null);

            var echoText = $"[Discord · Relay Test] {normalized}";
            var startedAt = DateTimeOffset.UtcNow;
            DeliveryOutcome outcome;
            string detail;
            try
            {
                outcome = await SendAndConfirmEchoAsync(echoText, "local-test", "local-test", cancellationToken).ConfigureAwait(false);
                detail = outcome == DeliveryOutcome.Sent
                    ? "Matching CWLS echo observed."
                    : "No authoritative CWLS echo was observed.";
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
                if (!configuration.ObserveToDiscordEnabled && !configuration.DiscordToGameEnabled)
                {
                    SetDisabled();
                }
                else if (!IsCoordinatorConfigured())
                {
                    SetDisconnected("Relay coordinator is not configured.");
                }
                else
                {
                    var heartbeat = await coordinator.HeartbeatAsync(runtimeInstanceId, cancellationToken).ConfigureAwait(false);
                    lock (gate)
                    {
                        coordinatorReachable = true;
                        role = heartbeat.Role;
                        epoch = heartbeat.Epoch;
                        leaseExpiresAtUtc = heartbeat.ExpiresAtUtc;
                        lastError = null;
                    }

                    await FlushObservationsAsync(cancellationToken).ConfigureAwait(false);
                    if (configuration.DiscordToGameEnabled &&
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
            lock (gate) observation = observations.FirstOrDefault();
            if (observation is null)
                return;

            await coordinator.PostObservationAsync(observation, cancellationToken).ConfigureAwait(false);
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
            var eligibility = await framework.RunOnTick(GetEligibility, cancellationToken: cancellationToken).ConfigureAwait(false);
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
                outcome = await SendAndConfirmEchoAsync(
                    echoText,
                    message.MessageId,
                    message.ClaimId,
                    cancellationToken).ConfigureAwait(false);
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

    private async Task<DeliveryOutcome> SendAndConfirmEchoAsync(
        string echoText,
        string messageId,
        string claimId,
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
            await framework.RunOnTick(
                () => Chat.SendMessage($"/cwl{configuration.CwlsSlot} {echoText}"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await echo.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                return DeliveryOutcome.Sent;
            }
            catch (OperationCanceledException)
            {
                return DeliveryOutcome.Ambiguous;
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
                    pendingSend = null;
                }
            }
        }
    }

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        var slot = CwlsChannels.ToSlot(chatMessage.LogKind);
        if (slot is null || slot.Value != configuration.CwlsSlot)
            return;

        var actualName = CwlsStateReader.ReadSlots().FirstOrDefault(value => value.Slot == slot.Value)?.Name;
        if (!IsExactSlotMatch(actualName))
            return;

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
                string.Equals(pendingEchoText, content, StringComparison.Ordinal) &&
                IsLocalPlayer(senderName))
            {
                echo = pendingEcho;
            }

            if (configuration.ObserveToDiscordEnabled)
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
                    observedAt);
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

    private Eligibility GetEligibility()
    {
        if (!clientState.IsLoggedIn || objectTable.LocalPlayer is null)
            return new Eligibility(false, "No logged-in local character is available.");
        if (configuration.CwlsSlot is < 1 or > 8 || string.IsNullOrWhiteSpace(configuration.ExpectedCwlsName))
            return new Eligibility(false, "An explicit CWLS slot and expected name are required.");
        var actualName = CwlsStateReader.ReadSlots().FirstOrDefault(value => value.Slot == configuration.CwlsSlot)?.Name;
        return IsExactSlotMatch(actualName)
            ? new Eligibility(true, "CWLS slot verified.")
            : new Eligibility(false, $"CWLS slot {configuration.CwlsSlot} is '{actualName ?? "unavailable"}', not '{configuration.ExpectedCwlsName}'.");
    }

    private bool IsExactSlotMatch(string? actualName) =>
        !string.IsNullOrWhiteSpace(actualName) &&
        string.Equals(actualName, configuration.ExpectedCwlsName, StringComparison.Ordinal);

    private bool IsLocalPlayer(string senderName) =>
        string.Equals(objectTable.LocalPlayer?.Name.TextValue, senderName, StringComparison.Ordinal);

    private bool IsCoordinatorConfigured()
    {
        try
        {
            _ = RelayCoordinatorClient.ValidateBaseUri(configuration.CoordinatorBaseUrl);
            return configuration.NodeId.Length is > 0 and <= 64 &&
                   configuration.NodeId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':') &&
                   !string.IsNullOrWhiteSpace(configuration.RelayProtectedAccessToken);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
            leaseExpiresAtUtc = null;
            lastError = error;
        }
    }

    private void SetDisabled()
    {
        lock (gate)
        {
            coordinatorReachable = false;
            role = "disabled";
            epoch = 0;
            leaseExpiresAtUtc = null;
            lastError = null;
        }
    }

    private static string ToWireName(DeliveryOutcome outcome) => outcome switch
    {
        DeliveryOutcome.Sent => "sent",
        DeliveryOutcome.NotSent => "not-sent",
        _ => "ambiguous",
    };

    private sealed record Eligibility(bool Allowed, string Reason);
}

internal sealed record RelayTestReceipt(
    bool Success,
    string Outcome,
    string Message,
    string? EchoText,
    DateTimeOffset? StartedAtUtc);
